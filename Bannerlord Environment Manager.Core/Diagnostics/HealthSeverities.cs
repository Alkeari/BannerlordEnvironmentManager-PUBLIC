using BannerlordEnvironmentManager.Core.LoadOrder;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// Every kind of finding the Health page lists from its own checks. The launch check's findings carry a
// grade of their own (PreflightGrades) and the load order's a severity of their own (IssueSeverities);
// these are the rest.
public enum HealthFindingKind
{
    DuplicateModuleId,
    SubModuleClassClash,
    HarmonyPatchIssue,
    MissingApi,
    AssemblyIssue,
    ShadowedModule,
    GameAssemblyCopy,
    RigIssue,
    RigConflict,
    XmlOverlapConflict,
    XmlOverlapOverride,
    FileCollision,
    PatchOrder,
    SafetyAlarm,
    DryRunFinding,
    LogException,
    LogError,
    LogStackFrame
}

// The severity of each, by the owner's definition: an Error will crash the game, definitively; a Warning
// may crash it or may or will make something work in a way nobody intended; a Note is everything else.
//
// Nothing here is an Error. Each of these checks reads files and infers, and none of them has been
// proven to end a run every time: the game loads the first folder of a duplicated id, constructs both
// submodules of a class named twice, and a Harmony patch that cannot apply may be caught by its mod. The
// findings that are proven come from the launch check, which grades them itself.
public static class HealthSeverities
{
    // The severity once recorded evidence is applied (FindingEvidence): a crash on this machine can raise
    // any finding to an Error, and a run that played fine takes an Error away. The row carries
    // verdict.Reason as its proof.
    public static IssueSeverity For(HealthFindingKind kind, FindingEvidenceVerdict verdict) =>
        verdict.Apply(For(kind));

    // Proofs, by finding (game tree: D:\Modding Reference\Bannerlord\Decompiled\bin):
    //   DuplicateModuleId: TaleWorlds.ModuleManager ModuleHelper.cs InitializeModules, first match wins.
    //   SubModuleClassClash: TaleWorlds.MountAndBlade Module.cs AddSubModule, _subModuleBases keyed by SubModuleInfo.
    //   AssemblyIssue: Module.cs LoadSubModules (DLLName CriticalError; Assemblies entry a box and LoadedWithErrors).
    //   ShadowedModule: ModuleHelper.cs GetPhysicalModules, the first folder wins.
    //   XmlOverlapConflict, XmlOverlapOverride: TaleWorlds.ObjectSystem MBObjectManager.cs MergeElements, the later module wins per the XSD.
    //   HarmonyPatchIssue, PatchOrder: depends on the mods' patch bodies; Harmony semantics, outside the game tree.
    //   MissingApi, GameAssemblyCopy: depends on the mod's use of the member; CLR binding, outside the game tree.
    //   RigIssue, RigConflict: native merge (soln_action_sets), not in the managed tree, so Warning at most.
    //   FileCollision: the game's virtual file system, no failure shown.
    //   SafetyAlarm: guards an outside scanner's verdict, not the game; Information.
    //   DryRunFinding, LogException, LogError, LogStackFrame: recorded facts of watched runs and logs, no game path.
    public static IssueSeverity For(HealthFindingKind kind) => kind switch
    {
        HealthFindingKind.DuplicateModuleId
            or HealthFindingKind.SubModuleClassClash
            or HealthFindingKind.HarmonyPatchIssue
            or HealthFindingKind.MissingApi
            or HealthFindingKind.AssemblyIssue
            or HealthFindingKind.GameAssemblyCopy
            or HealthFindingKind.RigIssue
            or HealthFindingKind.RigConflict
            or HealthFindingKind.XmlOverlapConflict
            or HealthFindingKind.PatchOrder
            // A dry run watched the game boot with these mods and recorded what went wrong. What it saw is
            // one run, so it says the next may go the same way, not that it must.
            or HealthFindingKind.DryRunFinding
            // An exception or a fatal line in a log: something did not do what its author wrote it to
            // do. Not proof of a crash, since a mod may have caught it and gone on.
            or HealthFindingKind.LogException => IssueSeverity.Warning,

        // A Workshop copy the game ignores because Modules already supplies that id. The game starts and
        // runs the copy in Modules every time, so nothing crashes and nothing is unpredictable; that the
        // subscribed copy is not the one in use is worth knowing and no more.
        HealthFindingKind.ShadowedModule
            // An outside scanner's verdict: no managed game code makes it a failure of the game.
            or HealthFindingKind.SafetyAlarm
            // A mod over the base game, or two mods shipping one file, is how the game is modded.
            or HealthFindingKind.XmlOverlapOverride
            or HealthFindingKind.FileCollision
            // A line the game logged as an error and nothing more. The base game logs these on a clean
            // install and plays on, so one says neither crash nor misbehaviour: worth reading, no more.
            or HealthFindingKind.LogError
            // A stack frame is context around a logged error, not a finding of its own.
            or HealthFindingKind.LogStackFrame => IssueSeverity.Information,

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "This finding has no severity; add it to HealthSeverities.For.")
    };
}
