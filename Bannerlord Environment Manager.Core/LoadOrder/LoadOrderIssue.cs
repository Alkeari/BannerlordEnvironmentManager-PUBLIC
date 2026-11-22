using BannerlordEnvironmentManager.Core.Diagnostics;
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

// The one place a load order diagnosis gets its severity. Error means the game is proven to crash, and
// no load order diagnosis is: the game's own loader (ModuleHelper.InitializeModules, Module.LoadSubModules
// in 1.4.8) never checks a dependency, an incompatibility or an order, skips an id it finds no folder for
// and takes the first folder that answers to a duplicated id. Each of those may crash the game or make a
// mod misbehave, which is a Warning. What changes nothing about how the game runs is a note.
public static class IssueSeverities
{
    // An order the author wrote down, or a module the order cannot be right without. An import or a
    // restored save reports these apart from the orders BEM only infers.
    public static bool IsStated(IssueKind kind) => kind
        is IssueKind.OrderViolation
        or IssueKind.CyclicDependency
        or IssueKind.MissingDependency
        or IssueKind.DisabledDependency
        or IssueKind.Incompatible;

    // Proofs, by diagnosis (game tree: D:\Modding Reference\Bannerlord\Decompiled\bin):
    //   MissingDependency, DisabledDependency, Incompatible, MissingOptionalDependency: Proof (Launcher):
    //     TaleWorlds.MountAndBlade.Launcher LauncherModsVM.cs AreAllDependenciesOfModulePresent and ChangeIsSelectedOf;
    //     the game itself checks none of them, so Warning at most.
    //   VersionMismatch: Proof (Launcher): LauncherVM.ExecuteStartGame compares ApplicationVersion.IsSame, a confirm dialog only.
    //   OrderViolation, ImpliedOrderViolation, InfrastructureAfterNative: depends on the mods' use of order; ModuleHelper.InitializeModules keeps the given order.
    //   CyclicDependency: TaleWorlds.Library MBMath.cs TopologySort marks visited and returns, no throw, so Information.
    //   OrphanEntry: ModuleHelper.InitializeModules finds no ModuleInfo and skips silently.
    //   UnreadableManifest: ModuleInfo.LoadWithFullPath throws, GetPhysicalModules catches: the module is dropped with a box.
    //   DuplicateModuleId: ModuleHelper.InitializeModules, first match wins.
    //   UnverifiedOfficialClaim: ModuleInfo.Type is read from SubModule.xml and never verified by the game.
    //   ContentBeforeNative: MBObjectManager.cs slot 0 of a merge; a missing first file is MergeFirstFileMissing.
    //   FolderWithoutManifest: ModuleHelper.GetPhysicalModules skips a folder with no SubModule.xml.
    public static IssueSeverity For(IssueKind kind) => kind switch
    {
        IssueKind.MissingDependency
            or IssueKind.DisabledDependency
            or IssueKind.Incompatible
            or IssueKind.OrderViolation
            or IssueKind.DuplicateModuleId
            or IssueKind.UnreadableManifest => IssueSeverity.Warning,

        // An order nobody stated, and a placement against Native, fire on the owner's own working
        // 242-module install, and the game's loader checks no order at all, so they are notes.
        // The game's loader never reads a dependency, so a cycle among them changes nothing it does; BEM only
        // cannot order such a set (ModuleHelper.InitializeModules keeps the order it is given).
        IssueKind.CyclicDependency
            or IssueKind.ImpliedOrderViolation
            or IssueKind.InfrastructureAfterNative
            or IssueKind.ContentBeforeNative
            or IssueKind.MissingOptionalDependency
            or IssueKind.VersionMismatch
            or IssueKind.OrphanEntry
            or IssueKind.UnverifiedOfficialClaim
            or IssueKind.FolderWithoutManifest => IssueSeverity.Information,

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "This diagnosis has no severity; add it to IssueSeverities.For.")
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

    // Optional dependencies not installed and declared versions that differ (31 notes on the owner's
    // install, none tied to a crash) are hidden by default, as is every other note.
    public FindingRelevance Relevance =>
        Severity == IssueSeverity.Information || Kind is IssueKind.MissingOptionalDependency or IssueKind.VersionMismatch
            ? FindingRelevance.Hidden
            : FindingRelevance.Relevant;
}
