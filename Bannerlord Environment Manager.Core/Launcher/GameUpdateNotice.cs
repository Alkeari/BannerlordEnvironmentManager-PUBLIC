using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Launcher;

// What a player is told when Steam has updated the game since their last run: the old and new game
// version, and each enabled mod that declares an older game version than the new one.
public sealed record GameUpdateNotice(
    ModuleVersion PreviousVersion,
    ModuleVersion CurrentVersion,
    IReadOnlyList<ModuleEntry> OutdatedMods)
{
    private static readonly ModuleId NativeId = new("Native");

    // Null when there is no recorded last run, the current version is unreadable, or nothing changed.
    public static GameUpdateNotice? Evaluate(
        string? lastLaunchedVersion,
        ModuleVersion currentVersion,
        IEnumerable<ModuleEntry> modules)
    {
        if (currentVersion.IsEmpty || !ModuleVersion.TryParse(lastLaunchedVersion, out var previous))
            return null;

        if (previous.CompareTo(currentVersion) == 0)
            return null;

        var outdated = modules
            .Where(entry => entry.IsEnabled && entry.Manifest is not null && !entry.IsOfficial)
            .Where(entry => DeclaredGameVersion(entry.Manifest!) is { IsEmpty: false } declared && declared < currentVersion)
            .ToList();

        return new GameUpdateNotice(previous, currentVersion, outdated);
    }

    // The game version a mod declares is the version on its Native dependency; null when it declares none.
    public static ModuleVersion DeclaredGameVersion(ModuleManifest manifest)
    {
        var dependency = manifest.Dependencies.FirstOrDefault(d =>
            d.TargetId.Equals(NativeId) && !d.IsIncompatible && !d.IsOrderOnly && !d.VersionRange.IsAny);

        return dependency?.VersionRange.Floor ?? ModuleVersion.Empty;
    }
}
