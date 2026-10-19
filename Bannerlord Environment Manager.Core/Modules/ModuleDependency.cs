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
    bool IsOrderExplicit = false,
    // True when the only thing the author wrote down is where the target goes if it is there at all:
    // a ModulesToLoadAfterThis or ModulesToLoadBeforeThis entry. The engine ignores an id in those
    // elements that names nothing installed, so an absent target is not a missing requirement, and a
    // list pinning thirty mods the user does not have is not thirty errors.
    bool IsOrderOnly = false)
{
    public string Describe() => (IsIncompatible, IsOptional) switch
    {
        (true, _) => Strings.Current.Format("Core.Modules.Dependency.Describe.Incompatible", TargetId),
        (_, true) => Strings.Current.Format("Core.Modules.Dependency.Describe.Optional", TargetId, VersionRange),
        _ => Strings.Current.Format("Core.Modules.Dependency.Describe.Required", TargetId, VersionRange)
    };
}
