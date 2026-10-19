using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

public sealed record ModuleEntry(ModuleId Id, ModuleManifest? Manifest, bool IsEnabled, bool IsUnreadable = false)
{
    public bool IsOrphan => Manifest is null && !IsUnreadable;

    public bool IsOfficial => Manifest?.IsOfficial ?? false;

    // Null, not a fourth origin: an entry with no readable manifest has no folder to have come from,
    // and inventing one would put an orphan under a chip that claims it is installed.
    public ModuleOrigin? Origin => Manifest?.Origin;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Manifest?.Name) ? Id.Value : Manifest!.Name;

    public ModuleVersion Version => Manifest?.Version ?? ModuleVersion.Empty;

    public IReadOnlyList<ModuleDependency> Dependencies => Manifest?.Dependencies ?? [];

    // The enabled state to take when it arrives from something other than this row's own toggle: a
    // sweep, an imported .bmlist, a shared .bemprofile. Disabling an official module breaks the game
    // rather than the mod list, and enabling a module with no folder on disk writes an entry the game
    // cannot resolve, so neither is obeyed and the entry keeps what it has. A per-row toggle is the
    // user asking directly and never comes through here.
    public bool EnabledFromOutside(bool wanted) => IsOfficial || (wanted && IsOrphan) ? IsEnabled : wanted;
}
