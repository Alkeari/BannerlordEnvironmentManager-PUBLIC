using BannerlordEnvironmentManager.Core.Launcher;

namespace BannerlordEnvironmentManager.Core.DryRun;

// BEM's companion is never part of the user's saved load order. It goes on one run's command line and
// nowhere else, so an entry for it in LauncherData.xml can only have arrived by hand, from the days
// when the module appeared in the Environment list looking like a mod that needed enabling.
//
// Left there it is worse than untidy: the folder is removed when watching stops, and the entry then
// points at nothing. Taking it out is the only write BEM ever makes to that file on its own, it takes
// out nothing but BEM's own ids, and every other entry keeps the element it already had, so nothing
// the user set and nothing BEM does not model is disturbed.
public static class CompanionLoadOrder
{
    public static IReadOnlyList<string> Find(LauncherDataStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        return [.. store.Read()
            .Select(entry => entry.Id.Value)
            .Where(CompanionManifest.IsCompanionId)];
    }

    public static IReadOnlyList<string> Prune(LauncherDataStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var entries = store.Read();
        var mine = entries
            .Select(entry => entry.Id.Value)
            .Where(CompanionManifest.IsCompanionId)
            .ToList();

        if (mine.Count == 0)
            return [];

        store.Write([.. entries.Where(entry => !CompanionManifest.IsCompanionId(entry.Id.Value))]);

        return mine;
    }
}
