using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules;

public enum DependencyOrder
{
    None,
    LoadBeforeThis,
    LoadAfterThis
}

public sealed record ModuleDependency(
    ModuleId TargetId,
    DependencyOrder Order,
    ModuleVersionRange VersionRange,
    bool IsOptional,
    bool IsIncompatible,
    // True when the author stated the ordering outright, in ModulesToLoadAfterThis,
    // ModulesToLoadBeforeThis or a DependedModuleMetadata carrying an order attribute. False when it is
    // only implied by a plain DependedModule, which says what is needed rather than where it goes.
    bool IsOrderExplicit = false)
{
    public string Describe() => (IsIncompatible, IsOptional) switch
    {
        (true, _) => Strings.Current.Format("Core.Modules.Dependency.Describe.Incompatible", TargetId),
        (_, true) => Strings.Current.Format("Core.Modules.Dependency.Describe.Optional", TargetId, VersionRange),
        _ => Strings.Current.Format("Core.Modules.Dependency.Describe.Required", TargetId, VersionRange)
    };
}
