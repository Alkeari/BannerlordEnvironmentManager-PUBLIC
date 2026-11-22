using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Saves;

public enum SaveSetupModStatus
{
    // In the copy at the version the save recorded.
    Restored,
    // One of the game's own modules: the copy's game version brought it.
    CameWithGame,
    // The recorded version could not be had, so the copy carries the version installed here instead.
    KeptInstalledVersion,
    // Nexus lists the file, but serves it to BEM only for a Premium account: the player downloads it.
    NeedsManualDownload,
    // Not in the copy at all: nothing here to copy and nothing on Nexus to fetch.
    Missing,
    Failed,
    Canceled
}

public sealed record SaveSetupModResult(
    ModuleId Id,
    string DisplayName,
    SaveSetupModStatus Status,
    string Message,
    // The Nexus page a player can finish the job from by hand, where there is one.
    string? Link = null);

public sealed record SaveSetupInstanceOutcome(InstalledInstance? Instance, string Message);

public sealed record SaveSetupFetchOutcome(bool Installed, bool NeedsPremium, string Message);

public sealed record SaveSetupProgress(string Message, int Done, int Total, double? Fraction = null);

public sealed record SaveSetupResult(
    InstalledInstance? Instance,
    IReadOnlyList<SaveSetupModResult> Mods,
    string InstanceMessage,
    string? CopiedSavePath,
    bool Canceled)
{
    public int Restored => Mods.Count(m => m.Status is SaveSetupModStatus.Restored);

    public bool InstanceReady => Instance is not null;

    // Every mod the save recorded is in the copy at the version it recorded.
    public bool Complete => InstanceReady && !Canceled
        && Mods.All(m => m.Status is SaveSetupModStatus.Restored or SaveSetupModStatus.CameWithGame);
}

// What actually moves bytes. Kept behind an interface so the order of operations, the fallbacks and
// the result for every mod are decided here and tested here, while the work itself goes through the
// paths the rest of BEM already uses: the version download, the Nexus fetch with its Premium rule and
// the safety-checked install pipeline.
public interface ISaveSetupActions
{
    Task<SaveSetupInstanceOutcome> PrepareInstanceAsync(
        SaveSetupTarget target, IProgress<SaveSetupProgress>? progress, CancellationToken cancellationToken);

    Task<SaveSetupFetchOutcome> FetchAsync(
        SaveRollbackStep step, InstalledInstance instance, CancellationToken cancellationToken);

    // Returns null on success, or the reason it failed.
    Task<string?> CopyInstalledAsync(
        SaveRollbackStep step, InstalledInstance instance, CancellationToken cancellationToken);

    // Returns where the copy of the save landed.
    Task<string> CopySaveAsync(SaveFile save, InstalledInstance instance, CancellationToken cancellationToken);
}

// Carries out a restore plan: the instance first, because every mod is written into it; the mods in
// the order the save recorded them; then a copy of the save into the instance's own saves folder so
// it can be loaded there. The save itself is only ever read. One mod's failure ends that mod and
// nothing else, and a stop leaves every mod not yet reached reported as stopped rather than missing.
public static class SaveSetupRestore
{
    public static async Task<SaveSetupResult> RunAsync(
        SaveFile save,
        SaveRollbackPlan plan,
        SaveSetupTarget target,
        ISaveSetupActions actions,
        IProgress<SaveSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(actions);

        var results = new List<SaveSetupModResult>();
        var total = plan.Steps.Count;

        if (!target.CanRestore || !plan.CanPlan)
        {
            return new SaveSetupResult(null, Unreached(plan.Steps, SaveSetupModStatus.Canceled), target.Reason, null,
                Canceled: false);
        }

        SaveSetupInstanceOutcome prepared;

        try
        {
            prepared = await actions.PrepareInstanceAsync(target, progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SaveSetupResult(null, Unreached(plan.Steps, SaveSetupModStatus.Canceled),
                Strings.Current["Core.Saves.Setup.Stopped"], null, Canceled: true);
        }

        if (prepared.Instance is not { } instance)
        {
            return new SaveSetupResult(null, Unreached(plan.Steps, SaveSetupModStatus.Canceled), prepared.Message,
                null, Canceled: cancellationToken.IsCancellationRequested);
        }

        var premiumRefused = false;

        foreach (var step in plan.Steps)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(Result(step, SaveSetupModStatus.Canceled, Strings.Current["Core.Saves.Setup.Mod.Stopped"]));
                continue;
            }

            progress?.Report(new SaveSetupProgress(
                Strings.Current.Format("Core.Saves.Setup.Progress.Mod", step.DisplayName), results.Count, total));

            try
            {
                results.Add(await RestoreAsync(step, instance, actions, premiumRefused, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results.Add(Result(step, SaveSetupModStatus.Canceled, Strings.Current["Core.Saves.Setup.Mod.Stopped"]));
                continue;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                results.Add(Result(step, SaveSetupModStatus.Failed, ex.Message));
            }

            if (results[^1].Status is SaveSetupModStatus.NeedsManualDownload)
                premiumRefused = true;
        }

        string? copiedSave = null;

        if (!cancellationToken.IsCancellationRequested)
        {
            progress?.Report(new SaveSetupProgress(Strings.Current["Core.Saves.Setup.Progress.Save"], total, total));

            try
            {
                copiedSave = await actions.CopySaveAsync(save, instance, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                prepared = prepared with
                {
                    Message = Strings.Current.Format("Core.Saves.Setup.SaveNotCopied", prepared.Message, ex.Message)
                };
            }
        }

        return new SaveSetupResult(instance, results, prepared.Message, copiedSave,
            cancellationToken.IsCancellationRequested);
    }

    private static async Task<SaveSetupModResult> RestoreAsync(
        SaveRollbackStep step,
        InstalledInstance instance,
        ISaveSetupActions actions,
        bool premiumRefused,
        CancellationToken cancellationToken)
    {
        switch (step.Action)
        {
            case SaveRollbackAction.FollowsGame:
                return Result(step, SaveSetupModStatus.CameWithGame, Strings.Current["Core.Saves.Setup.Mod.CameWithGame"]);

            case SaveRollbackAction.AlreadyMatches:
                return await CopyAsync(step, instance, actions, SaveSetupModStatus.Restored,
                    Strings.Current.Format("Core.Saves.Setup.Mod.CopiedAtRecorded", step.SaveVersion), cancellationToken);

            case SaveRollbackAction.GetFromNexus:
            {
                // Once Nexus has said this account cannot be served files unattended, asking again for
                // every other mod would spend a request each to hear the same answer.
                var fetched = premiumRefused
                    ? new SaveSetupFetchOutcome(false, true, string.Empty)
                    : await actions.FetchAsync(step, instance, cancellationToken);

                if (fetched.Installed)
                {
                    return Result(step, SaveSetupModStatus.Restored,
                        Strings.Current.Format("Core.Saves.Setup.Mod.Fetched", step.File?.Version ?? step.SaveVersion));
                }

                var reason = fetched.NeedsPremium
                    ? Strings.Current.Format("Core.Saves.Setup.Mod.NeedsPremium", step.File?.Version ?? step.SaveVersion)
                    : Strings.Current.Format("Core.Saves.Setup.Mod.FetchFailed", fetched.Message);

                var status = fetched.NeedsPremium ? SaveSetupModStatus.NeedsManualDownload : SaveSetupModStatus.Failed;

                return await StandInAsync(step, instance, actions, status, reason, cancellationToken);
            }

            case SaveRollbackAction.Workshop:
                return await StandInAsync(step, instance, actions, SaveSetupModStatus.Missing,
                    Strings.Current["Core.Saves.Setup.Mod.Workshop"], cancellationToken);

            case SaveRollbackAction.NexusFileNotListed:
                return await StandInAsync(step, instance, actions, SaveSetupModStatus.Missing,
                    Strings.Current.Format("Core.Saves.Setup.Mod.NotListed", step.SaveVersion), cancellationToken);

            default:
                return await StandInAsync(step, instance, actions, SaveSetupModStatus.Missing,
                    Strings.Current["Core.Saves.Setup.Mod.NoNexusPage"], cancellationToken);
        }
    }

    // The recorded version is out of reach, so the version installed here goes into the copy in its
    // place: a module missing from a save's list stops it loading far more often than a module at
    // another version does. Where nothing is installed either, the reason stands alone. A stand-in
    // keeps the status it was given when it is the player's to fix (a manual download), because the
    // copied version is a placeholder until they do.
    private static async Task<SaveSetupModResult> StandInAsync(
        SaveRollbackStep step,
        InstalledInstance instance,
        ISaveSetupActions actions,
        SaveSetupModStatus status,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!step.CanCopyInstalled)
            return Result(step, status, reason);

        var failure = await actions.CopyInstalledAsync(step, instance, cancellationToken);

        if (failure is not null)
            return Result(step, status, Strings.Current.Format("Core.Saves.Setup.Mod.StandInFailed", reason, failure));

        var kept = Strings.Current.Format("Core.Saves.Setup.Mod.StandIn", reason, step.InstalledVersion ?? string.Empty);

        return Result(step, status is SaveSetupModStatus.Missing ? SaveSetupModStatus.KeptInstalledVersion : status, kept);
    }

    private static async Task<SaveSetupModResult> CopyAsync(
        SaveRollbackStep step,
        InstalledInstance instance,
        ISaveSetupActions actions,
        SaveSetupModStatus status,
        string success,
        CancellationToken cancellationToken)
    {
        if (!step.CanCopyInstalled)
            return Result(step, SaveSetupModStatus.Missing, Strings.Current["Core.Saves.Setup.Mod.NoNexusPage"]);

        var failure = await actions.CopyInstalledAsync(step, instance, cancellationToken);

        return failure is null
            ? Result(step, status, success)
            : Result(step, SaveSetupModStatus.Failed, failure);
    }

    private static SaveSetupModResult Result(SaveRollbackStep step, SaveSetupModStatus status, string message) =>
        new(step.Id, step.DisplayName, status, message,
            status is SaveSetupModStatus.NeedsManualDownload or SaveSetupModStatus.Failed or SaveSetupModStatus.Missing
                or SaveSetupModStatus.KeptInstalledVersion
                ? step.NexusFilePage
                : null);

    private static List<SaveSetupModResult> Unreached(IEnumerable<SaveRollbackStep> steps, SaveSetupModStatus status) =>
        [.. steps.Select(step => Result(step, status, Strings.Current["Core.Saves.Setup.Mod.NotReached"]))];
}
