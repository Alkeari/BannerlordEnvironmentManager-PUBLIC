namespace BannerlordEnvironmentManager.Core.LoadOrder;

// What a right-clicked module row can and cannot do. A null target means the click landed on
// nothing, which is the only state in which every action is unavailable at once.
public sealed record ModuleContextTarget(
    bool IsOrphan,
    int Index,
    int Count,
    bool HasFolder,
    bool HasManifestFile,
    bool HasModPage);

public static class ModuleContextActions
{
    public static bool CanToggle(ModuleContextTarget? target) => target is { IsOrphan: false };

    public static bool CanMoveUp(ModuleContextTarget? target) => target is not null && target.Index > 0;

    public static bool CanMoveDown(ModuleContextTarget? target) =>
        target is not null && target.Index >= 0 && target.Index < target.Count - 1;

    public static bool CanOpenFolder(ModuleContextTarget? target) => target is { HasFolder: true };

    public static bool CanOpenManifest(ModuleContextTarget? target) => target is { HasManifestFile: true };

    public static bool CanCopyId(ModuleContextTarget? target) => target is not null;

    public static bool CanOpenModPage(ModuleContextTarget? target) => target is { HasModPage: true };

    public static bool CanPrune(ModuleContextTarget? target, bool isLoaded) =>
        isLoaded && target is { IsOrphan: true };

    public static bool CanRunDependencyClosure(ModuleContextTarget? target, bool isLoaded) =>
        isLoaded && target is not null;
}
