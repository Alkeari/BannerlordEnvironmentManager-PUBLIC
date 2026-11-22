namespace BannerlordEnvironmentManager.Core.DryRun;

// A game folder BEM knows about and the version whose folder it is. The label is what the user sees
// on Versions, so a companion found here can be named with the version it belongs to rather than a
// path they have to map back to a version themselves. An empty label is a folder no instance claims.
public sealed record CompanionSearchTarget(string GameFolder, string VersionLabel);

public sealed record CompanionLeftover(string GameFolder, string VersionLabel, IReadOnlyList<string> Folders);

public sealed record CompanionLeftoverRemoval(
    string GameFolder,
    string VersionLabel,
    IReadOnlyList<string> Folders,
    bool Removed,
    IReadOnlyList<string> RemainingFolders,
    string? Error);

// BEM installs its companion into whichever game folder a run used, and it knows about several: the
// install it did not download and every version it did. Looking for the leftover in only the active
// one made a folder in any other invisible to the check and unreachable by the button that removes
// it, so whether the warning appeared at all depended on which version happened to be selected.
public static class CompanionLeftoverScan
{
    public static IReadOnlyList<CompanionLeftover> Find(
        IEnumerable<CompanionSearchTarget> targets, WatchState watch)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(watch);

        var found = new List<CompanionLeftover>();

        foreach (var target in Distinct(targets))
        {
            // A companion in the folder an armed watch names is where it belongs. The watch names one
            // folder, so a companion in any other is still litter even while that watch is running.
            if (IsWatched(target.GameFolder, watch))
                continue;

            if (new CompanionInstaller(target.GameFolder).InstalledFolders is { Count: > 0 } folders)
                found.Add(new CompanionLeftover(target.GameFolder, target.VersionLabel, folders));
        }

        return found;
    }

    // Every location is attempted whatever the ones before it did: one game folder holding the
    // assembly open must not leave the others reported as untouched when they were removed fine.
    public static IReadOnlyList<CompanionLeftoverRemoval> Remove(IEnumerable<CompanionLeftover> leftovers)
    {
        ArgumentNullException.ThrowIfNull(leftovers);

        var removals = new List<CompanionLeftoverRemoval>();

        foreach (var leftover in leftovers)
        {
            var outcome = new CompanionInstaller(leftover.GameFolder).RemoveAll();

            removals.Add(new CompanionLeftoverRemoval(
                leftover.GameFolder,
                leftover.VersionLabel,
                leftover.Folders,
                outcome.RemainingFolders.Count == 0,
                outcome.RemainingFolders,
                outcome.Error));
        }

        return removals;
    }

    // The same folder reached under two names is one folder, and reporting it twice would ask the
    // owner to clear it twice. The first target wins, which is why the caller lists the registered
    // instances before whatever install is merely configured.
    private static IEnumerable<CompanionSearchTarget> Distinct(IEnumerable<CompanionSearchTarget> targets)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.GameFolder))
                continue;

            if (seen.Add(Canonical(target.GameFolder)))
                yield return target;
        }
    }

    private static bool IsWatched(string gameFolder, WatchState watch) =>
        !watch.IsEmpty
        && string.Equals(
            Canonical(watch.GameInstallPath), Canonical(gameFolder), StringComparison.OrdinalIgnoreCase);

    private static string Canonical(string folder)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return folder;
        }
    }
}
