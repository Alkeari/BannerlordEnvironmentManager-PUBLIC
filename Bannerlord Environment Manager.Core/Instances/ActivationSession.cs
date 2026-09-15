using BannerlordEnvironmentManager.Core.Io;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Instances;

// Junctions live only for the length of a launch. Between launches the canonical paths are ordinary
// folders holding the resting instance's data, which is what makes a launch from Steam or a desktop
// shortcut harmless: it can only ever reach the resting instance, never another version's store.
//
// Every path move here is synchronous filesystem work that regularly crosses volumes, since the games
// root sits on the drive with the most free space while Documents stays on the system one. It runs on
// a worker rather than the caller's thread: on a UI thread it froze the whole window for as long as the
// copy took, and the teardown after a run must not hold the caller at all.
public sealed class ActivationSession(
    CanonicalPathSet paths,
    string restingInstanceFolder,
    TimeSpan? teardownBudget = null,
    Action<string>? log = null)
{
    // How long the caller waits on the teardown before being handed a result and let go. The teardown
    // itself is not canceled: it keeps running and keeps the activation lock with it, so nothing else
    // can activate underneath it. The budget decides when the caller stops blocking on a bare await and
    // is handed something it can say out loud; the teardown itself comes back as ActivationResult's
    // Releasing, because a caller that stopped waiting and re-enabled its button offered a press the
    // lock then refused.
    public static readonly TimeSpan DefaultTeardownBudget = TimeSpan.FromMinutes(2);

    private TimeSpan Budget => teardownBudget ?? DefaultTeardownBudget;

    public async Task<ActivationResult> RunAsync(
        string targetInstanceFolder,
        Func<CancellationToken, Task> launch,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetInstanceFolder);
        ArgumentNullException.ThrowIfNull(launch);

        var activation = ActivationLock.Acquire(paths);
        var lockHandedToTeardown = false;

        try
        {
            RefuseIfAnythingIsAlreadyLive();

            await Task.Run(FinishInterruptedRestores, CancellationToken.None);

            if (string.Equals(
                    Path.GetFullPath(targetInstanceFolder),
                    Path.GetFullPath(restingInstanceFolder),
                    StringComparison.OrdinalIgnoreCase))
            {
                await launch(cancellationToken);

                return new ActivationResult(true, false, Strings.Current["Core.Instances.Activation.LaunchedInPlace"]);
            }

            var applied = new List<CanonicalPathPair>();

            try
            {
                // A pair is recorded before Apply runs, not after it returns. Apply itself moves the
                // resting data out before creating the junction, so a failure inside Apply (Create
                // included) can still leave that pair's data parked in its store with nothing at the
                // live path; recording early means the rollback below still finds and restores it.
                log?.Invoke($"Activating '{targetInstanceFolder}': moving the canonical folders into place.");

                await Task.Run(() =>
                {
                    foreach (var pair in paths.Pairs)
                    {
                        applied.Add(pair);
                        Apply(pair, targetInstanceFolder);
                    }
                }, CancellationToken.None);

                log?.Invoke($"Activated '{targetInstanceFolder}': {applied.Count} canonical path(s) junctioned.");
            }
            catch (Exception ex)
            {
                log?.Invoke($"Activation failed, putting the canonical folders back: {ex.Message}");
                await Task.Run(() => RestoreAll(applied, ex), CancellationToken.None);
                throw;
            }

            try
            {
                await launch(cancellationToken);
            }
            catch (Exception ex)
            {
                log?.Invoke($"The launch under '{targetInstanceFolder}' failed, putting the canonical folders back: {ex.Message}");
                await Task.Run(() => RestoreAll(applied, ex), CancellationToken.None);
                throw;
            }

            log?.Invoke("Junction teardown started.");

            var teardown = Task.Run(() => RestoreAll(applied), CancellationToken.None);

            if (await BoundedWait.FinishedWithinAsync(teardown, Budget))
            {
                // Awaited rather than assumed: RestoreAll reports the paths it could not put back by
                // throwing, and that is still the caller's to handle exactly as it was before.
                await teardown;

                log?.Invoke("Junction teardown finished; the canonical folders are back.");

                return new ActivationResult(true, true, Strings.Current.Format("Core.Instances.Activation.RanWithData", targetInstanceFolder));
            }

            lockHandedToTeardown = true;

            return new ActivationResult(
                true,
                true,
                Strings.Current.Format("Core.Instances.Activation.StillReleasing", (int)Budget.TotalSeconds),
                JunctionsReleased: false,
                Releasing: WatchOverrunningTeardown(teardown, activation));
        }
        finally
        {
            if (!lockHandedToTeardown)
                activation.Dispose();
        }
    }

    // The teardown outlived its budget, so the caller has been let go and this owns the lock now. The
    // lock is released only once the teardown really is finished, or another launch would start on top
    // of folders still being moved.
    //
    // What comes back is not the teardown itself but the whole of it including that release: a caller
    // awaiting the bare teardown would come back while the lock was still being let go and offer a
    // press that the lock then refused. The failure is rethrown, because a teardown that could not put
    // a path back is the caller's to report exactly as it is when the teardown finishes in time.
    private Task WatchOverrunningTeardown(Task teardown, ActivationLock activation)
    {
        var released = Release();

        // Observed here as well as by whoever awaits it, so a caller that ignores it leaves no
        // unobserved fault behind. Observing does not consume it: an await still throws.
        _ = released.ContinueWith(
            finished => _ = finished.Exception, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        return released;

        async Task Release()
        {
            try
            {
                await teardown.ConfigureAwait(false);

                log?.Invoke("Junction teardown finished after running past its budget; the canonical folders are back.");
            }
            catch (Exception ex)
            {
                log?.Invoke($"Junction teardown finished with a failure: {ex.Message}");
                throw;
            }
            finally
            {
                activation.Dispose();
            }
        }
    }

    // Two windows both activating is the isolation break this whole design exists to prevent: the
    // second game would run writing into the resting instance's data. A junction already standing means
    // either another session owns it or an interrupted one left it, and neither is something to repoint
    // underneath.
    private void RefuseIfAnythingIsAlreadyLive()
    {
        foreach (var pair in paths.Pairs)
        {
            if (!JunctionManager.IsJunction(pair.LivePath))
                continue;

            throw new IOException(
                $"'{pair.LivePath}' is already a junction to another version's data, so no launch was "
                + "started. Close the game if it is running, then restart BEM, which puts the canonical "
                + "paths back at startup.");
        }
    }

    // A restore that failed in an earlier teardown or rollback leaves a staged copy, or the resting data
    // parked in its store, and neither is a junction for the check above to see. Activating over it
    // either runs the game with nothing at the canonical path or refuses to move the live folder onto a
    // store that already holds the data, so it is settled first, under the lock this launch already holds.
    private void FinishInterruptedRestores()
    {
        foreach (var pair in paths.Pairs)
        {
            if (!CanonicalRestore.NeedsRestore(pair, restingInstanceFolder))
                continue;

            log?.Invoke($"'{pair.LivePath}' was left mid-restore by an earlier run; finishing that before activating.");
            CanonicalRestore.Restore(pair, restingInstanceFolder);
        }
    }

    // Every applied pair is attempted regardless of whether an earlier one failed: a launch or an
    // apply failure must not abandon three good paths because the fourth could not be put back.
    // Failures are collected and reported together afterward, matching how StartupRepair reports
    // the paths it could not fix rather than stopping at the first one.
    private void RestoreAll(IReadOnlyList<CanonicalPathPair> restoring, Exception? cause = null)
    {
        var failed = new List<(CanonicalPathPair Pair, Exception Error)>();

        foreach (var pair in Enumerable.Reverse(restoring))
        {
            try
            {
                CanonicalRestore.Restore(pair, restingInstanceFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"Could not put '{pair.LivePath}' back: {ex}");
                failed.Add((pair, ex));
            }
        }

        if (failed.Count == 0)
            return;

        // The state of each failed path is read off disk rather than assumed. Saying they still point at
        // another version's data when the restore never got that far would send the user looking for the
        // wrong problem. Each path's own reason is named beside it, because a restore that failed over a
        // locked file and one that refused over two differing folders call for different things.
        var described = failed.Select(failure =>
            $"{CanonicalRestore.Describe(failure.Pair, restingInstanceFolder)} ({failure.Error.Message})");

        throw new IOException(
            $"{failed.Count} canonical path(s) could not be put back: {string.Join("; ", described)}. "
            + "Close anything using them and restart BEM, which repairs this at startup.",
            cause ?? failed[0].Error);
    }

    private void Apply(CanonicalPathPair pair, string targetInstanceFolder)
    {
        var restingStore = InstanceLayout.StoreFolder(restingInstanceFolder, pair.StoreFolderName);
        var targetStore = InstanceLayout.StoreFolder(targetInstanceFolder, pair.StoreFolderName);

        Directory.CreateDirectory(targetStore);

        if (Directory.Exists(pair.LivePath))
            MoveInto(pair.LivePath, restingStore);

        JunctionManager.Create(pair.LivePath, targetStore);
    }

    // Move rather than copy: the resting data and the live folder are the same bytes taking turns at
    // two paths, and copying would double a save folder on every launch. The move goes through
    // DirectoryMover because Directory.Move is a rename and refuses two roots, and the shipped default
    // puts the games root on the drive with the most free space while Documents stays on the system one.
    //
    // A destination that exists and holds files means both paths think they own the resting data. When
    // every byte of it is also in the live folder, it is the duplicate a failed restore left behind and
    // is dropped. Otherwise merging blind could bury a campaign under an older copy of itself, so it
    // stops instead and says which two folders disagree.
    private static void MoveInto(string from, string to)
    {
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any() && DirectoryContents.IsCoveredBy(to, from))
            DirectoryMover.DeleteTree(to);

        if (Directory.Exists(to))
        {
            if (Directory.EnumerateFileSystemEntries(to).Any())
                throw new IOException(
                    $"'{to}' already holds data, so '{from}' was not moved onto it. One of the two is a "
                    + "leftover from an interrupted session and has to be dealt with by hand.");

            Directory.Delete(to, recursive: false);
        }

        DirectoryMover.Move(from, to);
    }
}
