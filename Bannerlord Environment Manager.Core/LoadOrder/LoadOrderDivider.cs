using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// A collapsible, renamable section marker for the Play tab's manual load order. It never becomes a
// ModuleEntry and never reaches LauncherData.xml: nothing that decides what the game actually boots
// is allowed to know this exists. AnchorId names the module this divider sits directly above; null
// means "at the very end of the list", which is also what Auto-Sort's default behavior sets every
// divider to (see LoadOrderDividerAutoSortPolicy). SourceModuleId is set only when this divider was
// recognized from a real installed divider-* module folder, so a later re-scan does not duplicate it.
public sealed record LoadOrderDivider(
    string Label,
    ModuleId? AnchorId,
    bool Collapsed = false,
    ModuleId? SourceModuleId = null);
