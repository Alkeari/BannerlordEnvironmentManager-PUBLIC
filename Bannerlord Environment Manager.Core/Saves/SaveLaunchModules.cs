using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Saves;

// A save records only the game-mode module its session used: a sandbox save lists SandBox and never
// StoryMode. War Sails requires StoryMode, so launching a sandbox save with exactly its recorded list
// leaves NavalDLC.GauntletUI unable to load StoryMode.GauntletUI, and the game dies seconds later
// (seen 2026-11-09 on the v1.4.8 + War Sails instance). Every installed module that a listed module
// requires and the list lacks is added just ahead of the first module needing it, repeated until
// nothing new is needed.
public enum SaveLaunchMode
{
    // The save's recorded list, completed with what it requires.
    FromSave,

    // The Play page's enabled modules in their load order, whatever the save recorded.
    CurrentOrder
}

public static class SaveLaunchModules
{
    public static IReadOnlyList<SaveModuleRecord> For(
        SaveLaunchMode mode, IReadOnlyList<SaveModuleRecord> recorded, IReadOnlyList<ModuleEntry> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);

        return mode == SaveLaunchMode.CurrentOrder
            ? [.. installed.Where(e => e.IsEnabled).Select(e => new SaveModuleRecord(e.Id, e.Manifest?.Version.ToString() ?? string.Empty))]
            : Complete(recorded, installed);
    }

    // Names the launch warning shows before the game starts. A save-order launch turns an installed
    // module back on from the save's own list, so only a module that is no longer installed is named.
    // A current-order launch sends only what is enabled now, so every module the save used and this
    // list leaves out is missing for the run, installed or not.
    public static IReadOnlyList<string> WarnedNames(
        SaveLaunchMode mode, IReadOnlyList<SaveModuleDifference> removed)
    {
        ArgumentNullException.ThrowIfNull(removed);

        return removed
            .Where(r => mode == SaveLaunchMode.CurrentOrder || !r.IsInstalled)
            .Select(r => r.DisplayName)
            .ToList();
    }

    public static IReadOnlyList<SaveModuleRecord> Complete(
        IReadOnlyList<SaveModuleRecord> recorded, IReadOnlyList<ModuleEntry> installed)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(installed);

        var byId = new Dictionary<string, ModuleEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in installed)
        {
            if (entry.Manifest is not null)
                byId.TryAdd(entry.Id.Value, entry);
        }

        var list = recorded.ToList();
        bool added;

        do
        {
            added = false;

            for (var i = 0; i < list.Count; i++)
            {
                if (!byId.TryGetValue(list[i].Id.Value, out var entry))
                    continue;

                foreach (var dependency in entry.Dependencies)
                {
                    if (dependency.IsOptional || dependency.IsIncompatible || dependency.IsOrderOnly
                        || dependency.Order == DependencyOrder.LoadAfterThis)
                        continue;

                    var target = dependency.TargetId.Value;

                    if (list.Any(m => string.Equals(m.Id.Value, target, StringComparison.OrdinalIgnoreCase))
                        || !byId.TryGetValue(target, out var found))
                        continue;

                    list.Insert(i, new SaveModuleRecord(found.Id, found.Manifest!.Version.ToString()));
                    added = true;
                    break;
                }

                if (added)
                    break;
            }
        }
        while (added);

        return list;
    }
}
