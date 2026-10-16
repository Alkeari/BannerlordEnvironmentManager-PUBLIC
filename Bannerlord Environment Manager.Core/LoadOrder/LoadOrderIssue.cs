using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

public enum IssueSeverity
{
    Warning,
    Error,
    Information
}

public enum IssueKind
{
    MissingDependency,
    MissingOptionalDependency,
    DisabledDependency,
    VersionMismatch,
    OrderViolation,
    Incompatible,
    CyclicDependency,
    OrphanEntry,
    UnreadableManifest,
    DuplicateModuleId,
    UnverifiedOfficialClaim,
    // The ordering is implied by a plain dependency rather than stated outright, so it is graded below
    // OrderViolation, which is reserved for a declaration the author wrote as an order.
    ImpliedOrderViolation,
    InfrastructureAfterNative,
    ContentBeforeNative,
    // A folder under Modules with no SubModule.xml in it. The game loads nothing from one and every BUTR
    // library throws a caught FileNotFoundException over it once per launch.
    FolderWithoutManifest
}

// Three severity words stand for fourteen distinct diagnoses, and the severity is the part that says
// least. Every kind is named here, so a panel or a pane can show which diagnosis a module actually has
// rather than only how bad it is.
public static class IssueKinds
{
    public static string Describe(IssueKind kind) => kind switch
    {
        IssueKind.MissingDependency => Strings.Current["Core.LoadOrder.IssueKind.MissingDependency"],
        IssueKind.MissingOptionalDependency => Strings.Current["Core.LoadOrder.IssueKind.MissingOptionalDependency"],
        IssueKind.DisabledDependency => Strings.Current["Core.LoadOrder.IssueKind.DisabledDependency"],
        IssueKind.VersionMismatch => Strings.Current["Core.LoadOrder.IssueKind.VersionMismatch"],
        IssueKind.OrderViolation => Strings.Current["Core.LoadOrder.IssueKind.OrderViolation"],
        IssueKind.Incompatible => Strings.Current["Core.LoadOrder.IssueKind.Incompatible"],
        IssueKind.CyclicDependency => Strings.Current["Core.LoadOrder.IssueKind.CyclicDependency"],
        IssueKind.OrphanEntry => Strings.Current["Core.LoadOrder.IssueKind.OrphanEntry"],
        IssueKind.UnreadableManifest => Strings.Current["Core.LoadOrder.IssueKind.UnreadableManifest"],
        IssueKind.DuplicateModuleId => Strings.Current["Core.LoadOrder.IssueKind.DuplicateModuleId"],
        IssueKind.UnverifiedOfficialClaim => Strings.Current["Core.LoadOrder.IssueKind.UnverifiedOfficialClaim"],
        IssueKind.ImpliedOrderViolation => Strings.Current["Core.LoadOrder.IssueKind.ImpliedOrderViolation"],
        IssueKind.InfrastructureAfterNative => Strings.Current["Core.LoadOrder.IssueKind.InfrastructureAfterNative"],
        IssueKind.ContentBeforeNative => Strings.Current["Core.LoadOrder.IssueKind.ContentBeforeNative"],
        _ => Strings.Current["Core.LoadOrder.IssueKind.FolderWithoutManifest"]
    };

    public static string Describe(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => Strings.Current["Core.LoadOrder.IssueSeverity.Error"],
        IssueSeverity.Warning => Strings.Current["Core.LoadOrder.IssueSeverity.Warning"],
        _ => Strings.Current["Core.LoadOrder.IssueSeverity.Note"]
    };
}

public sealed record LoadOrderIssue(
    IssueKind Kind,
    IssueSeverity Severity,
    ModuleId ModuleId,
    ModuleId? TargetId,
    string Message,
    Func<ModuleEnvironment, ModuleEnvironment>? Fix = null,
    // A destructive fix removes user data (e.g. pruning an orphan) rather than merely
    // reordering or re-enabling something already on disk. FixAll must never apply one
    // automatically; only an explicit, single-issue Fix or a dedicated bulk command may.
    bool IsDestructive = false,
    // The folder this issue is about, where it is about one. Carried structurally rather than left to
    // be read back out of Message, because it is what a removal would act on and matching an issue to a
    // folder by module id alone cannot separate two folders that share a name across scan roots.
    string? Path = null)
{
    public bool IsFixable => Fix is not null;

    public bool IsAutoFixable => IsFixable && !IsDestructive;
}
