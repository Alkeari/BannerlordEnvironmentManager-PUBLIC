using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.DryRun;

// What turning the suspects off changed: the names of every mod that went off, the suspects and whatever
// needed one of them, in load order.
public sealed record SuspectSwitchResult(IReadOnlyList<string> TurnedOff)
{
    public static SuspectSwitchResult Nothing { get; } = new([]);
}

// The Bannerlord Exception Manager's "Turn Off Suspect Mods": the mods on a failing stack switched off in
// the load order the game reads at its next start. The running game is not touched; it loaded its mods
// long ago. The game's own modules, BEM's module and the shared libraries every mod leans on are never
// switched off, because a stack that passes through Harmony says nothing against Harmony.
public static class SuspectSwitch
{
    // The suspects this can act on, from the ids the report named.
    public static IReadOnlyList<ModuleId> Roots(IReadOnlyList<string> suspectIds)
    {
        ArgumentNullException.ThrowIfNull(suspectIds);

        return [.. suspectIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && !CompanionManifest.IsCompanionId(id) && Prerequisites.ById(id) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => new ModuleId(id))];
    }

    // What would go off, without writing anything: changes nothing when no suspect is a mod that is on.
    public static DependencyClosure Plan(string gameFolder, LauncherDataStore store, IReadOnlyList<string> suspectIds) =>
        Plan(gameFolder, store, suspectIds, out _);

    // A mod that needs a suspect goes off with it. Left on, it would stop the next start on a missing
    // dependency, which trades one failure for another.
    public static SuspectSwitchResult TurnOff(
        string gameFolder, LauncherDataStore store, IReadOnlyList<string> suspectIds, string? backupReason)
    {
        var closure = Plan(gameFolder, store, suspectIds, out var environment);

        if (!closure.ChangesAnything)
            return SuspectSwitchResult.Nothing;

        var byId = environment.ById;

        store.Write(closure.ApplyTo(environment).ToLauncherEntries(), backupReason);

        return new SuspectSwitchResult([.. closure.WillChange.Select(id => byId[id].DisplayName)]);
    }

    private static DependencyClosure Plan(
        string gameFolder, LauncherDataStore store, IReadOnlyList<string> suspectIds, out ModuleEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(store);

        environment = ModuleEnvironment.Empty;

        var roots = Roots(suspectIds);

        if (roots.Count == 0 || string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder) || !store.Exists)
            return DependencyClosure.For(environment, [], DependencyClosureKind.DisableWhatNeedsIt);

        var scan = ModuleScanner.ScanAll(gameFolder);

        if (scan.Failed)
            return DependencyClosure.For(environment, [], DependencyClosureKind.DisableWhatNeedsIt);

        environment = ModuleEnvironment.Merge(scan, store.Read());

        var known = environment.ById;

        return DependencyClosure.For(
            environment, [.. roots.Where(known.ContainsKey)], DependencyClosureKind.DisableWhatNeedsIt);
    }
}
