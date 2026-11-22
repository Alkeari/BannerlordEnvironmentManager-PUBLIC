using BannerlordEnvironmentManager.Core.LoadOrder;

namespace BannerlordEnvironmentManager.Core.Launcher;

// What each severity means, in the owner's words, and the one table that says which checks may use the
// worst of them:
//
//   Error (Critical)  "THIS WILL CRASH YOUR GAME. DEFINITIVELY."
//   Warning           "This MAY crash your game OR This MAY/WILL function in an unintended fashion."
//   Note (Advisory)   "ALL ELSE", rendered as nothing to worry about.
//
// A check earns Critical by reading only by proof: the game's own source showing the failure is not
// caught, or a crash reproduced and traced to exactly that condition. A check that is right most of the
// time is a Warning, because an Error that turns out harmless teaches the owner to ignore the one that is
// not.
//
// Reading is one of two ways to an Error. The other is what was watched (LaunchEvidence): any finding a
// crash on this computer named, on the same mod file and game version, is an Error whatever its ceiling
// here, and any finding a launch carried and played through is not one, whatever its ceiling here.
public static class PreflightGrades
{
    // The worst grade a check may ever give. Individual findings of the check may be graded lower.
    public static PreflightGrade Ceiling(PreflightCheck check) => check switch
    {
        // The game copies the argument string into a fixed buffer and the C runtime kills the process when
        // it does not fit, before any mod loads.
        // Proof: TaleWorlds.Native.dll FUN_180026990 (Decompiled\native): strcpy_s into a 0x1000 buffer, call at 0x180026e2f.
        PreflightCheck.CommandLineLength

            // A patch names a type the game does not have, nothing guards it, and the mod applies its
            // patches from its submodule class with no catch. Harmony throws, the mod does not catch, and
            // the game's loader (Module.InitializeSubModuleBases) catches nothing.
            // Proof: TaleWorlds.MountAndBlade\Module.cs InitializeSubModuleBases: an OnSubModuleLoad throw is logged and rethrown as new Exception(). Depends on the mod applying the patch from OnSubModuleLoad or OnBeforeInitialModuleScreenSetAsRoot (Module.cs SetInitialModuleScreenAsRootScreen, bare loop); Harmony's own throw is library behavior outside the game tree.

            or PreflightCheck.HarmonyPatchTarget

            // The same, where the missing type or member is in a method Harmony runs to find the target.
            // Proven by Surrender Tweaks on the owner's 1.4.8 install, which died before the menu.
            // Proof: same loader path as HarmonyPatchTarget, Module.cs InitializeSubModuleBases rethrow.

            or PreflightCheck.PatchSetupMissingApi

            // A method the game is certain to run while it starts (StartupPath) uses a type or member the
            // install does not have. The runtime throws when it reaches it and the loader's loops catch
            // nothing (Module.AddSubModule, InitializeSubModuleBases, SetInitialModuleScreenAsRootScreen).
            // Proof: Module.cs AddSubModule (constructor.Invoke caught, CriticalError), InitializeSubModuleBases rethrow, SetInitialModuleScreenAsRootScreen bare loop; only methods on StartupPath, which the game runs unconditionally.

            or PreflightCheck.StartupMissingApi

            // Module.AddSubModule checks the class (GetType null, not an MBSubModuleBase, no parameterless
            // constructor) and returns CriticalError, which LoadSubModules collects and throws after the loop.
            // Proof: Module.cs AddSubModule returns CriticalError for a null type, a type not assignable to MBSubModuleBase, no parameterless constructor or a throwing constructor; LoadSubModules throws after its loop.

            or PreflightCheck.SubModuleClassMissing

            // Module.CollectModuleAssemblyTypes calls Assembly.GetTypes on the SubModule's file. A type
            // whose base or interface is gone makes that throw (run on both runtimes), the loader returns
            // CriticalError, and HandleSubmoduleLoadError answers that by throwing an exception of its own.
            // Proof: Module.cs CollectModuleAssemblyTypes (Assembly.GetTypes inside a catch that returns CriticalError), then LoadSubModules throws.

            or PreflightCheck.SubModuleTypeLoad

            // MBObjectManager.CreateMergedXmlFile opens the first file of a merge with no existence check,
            // and GetMergedXmlForManaged queues an empty path for a registration with nothing behind it.
            // Proof: TaleWorlds.ObjectSystem\MBObjectManager.cs GetMergedXmlForManaged queues an empty path and CreateMergedXmlFile does new StreamReader on slot 0, outside LoadXML's try.

            or PreflightCheck.MergeFirstFileMissing

            // The game calls RegisterEvents of a campaign behavior the mod constructs when it is added to a
            // campaign; the runtime throws MissingMethodException at the first use of a member that is not
            // there, on every path through the method, and nothing in the game's behavior loop catches it.
            // Proof: TaleWorlds.CampaignSystem\CampaignBehaviors\CampaignBehaviorManager.cs lines 32-36 and 79: RegisterEvents() with no try or catch. Depends on the mod constructing that behavior, which the check requires.

            or PreflightCheck.CampaignStartMissingApi

            // Assembly.GetTypes on a SubModule's file throws for a type that leaves a member of a game class
            // or interface unimplemented (graded Warning where the file is not a SubModule's).
            // Proof: Module.cs CollectModuleAssemblyTypes GetTypes, SubModule DLLName only. Pattern-matched files (BLSE LoaderFilter, not in the game tree) are graded Warning by the finding.

            or PreflightCheck.UnimplementedMember

            // TaleWorlds.MountAndBlade Module.cs LoadSubModules: a SubModule's DLLName that exists and will not
            // load (AssemblyLoader.LoadFrom answers CriticalError) is collected by HandleSubmoduleLoadError, and
            // LoadSubModules throws after the loop. Only that file is Critical; a plain <Assemblies> entry that
            // will not load is LoadedWithErrors, a message box, and the finding is graded Warning below Critical.
            // Proof: Module.cs LoadSubModules / HandleSubmoduleLoadError (CriticalError for a failing DLLName; TaleWorlds.Library AssemblyLoader.cs LoadFrom "Cannot load:" box for Assemblies entries).

            or PreflightCheck.InvalidDeclaredAssembly

            // TaleWorlds.ObjectSystem XmlResource.cs GetXmlListAndApply throws on an <XmlNode> with no
            // <XmlName>, an <XmlName> with no id or path attribute, or an <IncludedGameTypes> child with no
            // value attribute; TaleWorlds.MountAndBlade Module.cs LoadSubModules rethrows it out of its
            // module loop, so the boot dies.
            // Proof: TaleWorlds.ObjectSystem\XmlResource.cs GetXmlListAndApply throws; Module.cs LoadSubModules rethrows.

            or PreflightCheck.XmlNodeIncomplete

            // TaleWorlds.ModuleManager SubModuleInfo.cs LoadFrom throws on a <SubModule> missing Name, DLLName
            // or SubModuleClassType; ModuleInfo.cs keeps the half-built entry and Module.cs LoadSubModules
            // dereferences its null DLLName or Assemblies outside any try, so the boot dies.
            // Proof: TaleWorlds.ModuleManager\SubModuleInfo.cs LoadFrom throws, ModuleInfo.cs swallows and keeps the entry, Module.cs LoadSubModules dereferences it.

            or PreflightCheck.SubModuleIncomplete

            // TaleWorlds.ModuleManager ModuleHelper.cs InitializeModules: NavalDLC RequiredBaseVersion differing
            // from ApplicationVersion.FromParametersFile() shows a message box and calls Environment.Exit(0).
            // Proof: TaleWorlds.ModuleManager\ModuleHelper.cs InitializeModules (IsModuleVersionMismatched, Environment.Exit(0) at lines 124 and 394).

            or PreflightCheck.NavalDlcVersionMismatch

            // TaleWorlds.ObjectSystem MBObjectManager.cs ApplyXslt (XslCompiledTransform.Load) runs from
            // GetMergedXmlForManaged ahead of LoadXML's try, so an unparsable or uncompilable stylesheet ends
            // the dataset load.
            // Proof: MBObjectManager.cs ApplyXslt (line 980) called from GetMergedXmlForManaged ahead of LoadXML's try.

            or PreflightCheck.StylesheetInvalid

            // TaleWorlds.MountAndBlade Module.cs CollectModuleAssemblyTypes (types.Add(type.Name, type)) and
            // TaleWorlds.DotNet Managed.cs AddTypes (Union then ToDictionary) throw on a second ManagedObject
            // or DotNetObject type of the same simple name, which ends the load.
            // Proof: Module.cs CollectModuleAssemblyTypes types.Add(type.Name, type) line 189; TaleWorlds.DotNet\Managed.cs AddTypes Union().ToDictionary() line 546.

            // Harmony applies a patch by running the patched type's static constructor; one that reads GameTexts
            // throws while its text manager is still null (module load), and the runtime keeps that exception
            // for the type, so the game dies the first time it uses it. Read from the game's own IL, and seen
            // on his v1.5.7 set (Character Creation Next, CampaignUIHelper).
            // Proof: TaleWorlds.Core\GameTexts.cs FindText/TryGetText dereference _gameTextManager, set only by GameTexts.Initialize; TaleWorlds.CampaignSystem.ViewModelCollection\CampaignUIHelper.cs _partyMoraleStr = GameTexts.FindText(...) in its .cctor; CLR caches a failed type initializer. Depends on the patch being applied before the manager is set.

            or PreflightCheck.TextTableStaticInitializer
            or PreflightCheck.DuplicateManagedTypeName => PreflightGrade.Critical,

        // The game shows "Cannot find: <dll>" and goes on without the module (Module.LoadSubModules).
        // Proof: Module.cs LoadSubModules line 1155 "Cannot find:" box, the game goes on without the file (a DLLName also in the game's own bin folder loads, so it is not reported).
        PreflightCheck.MissingDeclaredAssembly

            // The loader checks no dependency, incompatibility or order, so none of these is a certain crash.
            // Proof: depends on what the mods do with the missing or misordered module; the game checks no dependency or order (ModuleHelper.InitializeModules). Only the Launcher does; see Proof (Launcher) at IssueSeverities.For.

            or PreflightCheck.LoadOrder

            // The game's XML loading catches a file that does not parse (MBObjectManager.LoadXmlWithValidation
            // and its callers), so whether a run dies depends on the file and who reads it.
            // Proof: MBObjectManager.cs LoadXmlWithValidation (catch at line 1120) leaves a truncated dataset; depends on which dataset and how far the parse got.

            or PreflightCheck.MalformedXml
            // Proof: TaleWorlds.ModuleManager ModuleInfo.LoadWithFullPath throws, ModuleHelper.GetPhysicalModules catches: the module is not loaded at all.

            or PreflightCheck.MalformedModuleXml

            // Built for another game line: the unresolved calls throw only when the code holding them runs.
            // Proof: depends on the mod's use of the missing member; the CLR throws only when the holding method runs (game assemblies bind by simple name).

            or PreflightCheck.BuiltForLine
            or PreflightCheck.MissingGameApi

            // Facts about past runs. A run that crashed is evidence, not a proof about the next one.
            // Proof: none by game code; these are recorded facts about past runs (logs, crash reports), correlation only.

            or PreflightCheck.LaunchHistory
            or PreflightCheck.CrashHistory
            or PreflightCheck.CrashCauseUnknown
            or PreflightCheck.EngineInvokeFailure
            // Proof: depends on the mod's writes to shared state; the game does run party ticks in TWParallel.For (CampaignTickCacheDataStore.cs 311-327, CampaignPeriodicEventManager.cs 72).

            or PreflightCheck.ParallelTickCorruption
            or PreflightCheck.CollectionModified
            // Proof: MBObjectManager.cs UnregisterNonReadyObjects line 1451 prints "Null object reference found with ID"; the cause attribution is inferred.

            or PreflightCheck.EngineDataLoad
            // Proof: guards Windows, not the game: the OS event log, no game code.

            or PreflightCheck.SmartAppControl
            // Proof: BLSE log fact, outside the game tree.

            or PreflightCheck.XboxHarmonyDependency
            or PreflightCheck.ExceptionManagerUnarmed

            // Things that change what a mod does without a proven crash.
            // Proof: MBObjectManager.cs CreateMergedXmlFile applies xsltList[i] for i from 1 (line 969); LoadXML swallows the outcome, so the dataset may load empty.

            or PreflightCheck.DatasetEmptiedByStylesheet
            // Proof: depends on the mod's use of the prerequisite; the game ignores DependedModule (only the Launcher reads it).

            or PreflightCheck.MissingPrerequisite
            // Proof: Module.cs AddSubModule keys _subModuleBases by SubModuleInfo (line 174), so a class named by two modules is constructed twice; the harm is the mod's.

            or PreflightCheck.DuplicateSubModuleClass
            // Proof: depends on the mod's use of the copy; assembly identity binding is the CLR's, not game code.

            or PreflightCheck.BundledStackCopy
            or PreflightCheck.ShadowedAssembly
            // Proof: depends on the mod's use of changed APIs; the game never compares a mod's version with its own except NavalDLC (ModuleHelper.IsModuleVersionMismatched).

            or PreflightCheck.GameVersion
            // Proof: rigs merge natively (soln_action_sets, native engine); the order effect is not in the managed tree, so Warning at most.

            or PreflightCheck.RigConflict
            // Proof: depends on both mods' patch bodies; Harmony prefix and postfix semantics, library behavior.

            or PreflightCheck.PatchOverlap
            or PreflightCheck.SkippedResultClash => PreflightGrade.Warning,

        // Nothing in the managed game can prove these: the render path and the shader loader are native, and
        // the safety result is an outside scanner's verdict with no game code behind it (check-audit.md).
        // They stay worth reading and never stand in front of the Launch button.
        // Proof: none in managed code; the render path and the shader loader are native, and ModSafety is an outside scanner's verdict.
        PreflightCheck.RenderRequestBurst
            or PreflightCheck.StaleShaderCache
            or PreflightCheck.ModSafety => PreflightGrade.Advisory,

        _ => throw new ArgumentOutOfRangeException(nameof(check), check, "This check has no severity ceiling; add it to PreflightGrades.Ceiling.")
    };

    // The sentence that says why a check graded Critical by reading is certain, shown on its row.
    public static string ProofKey(PreflightCheck check) => check switch
    {
        PreflightCheck.CommandLineLength => "Core.Launcher.Preflight.Proof.Read.CommandLine",
        PreflightCheck.HarmonyPatchTarget or PreflightCheck.PatchSetupMissingApi => "Core.Launcher.Preflight.Proof.Read.Patching",
        PreflightCheck.StartupMissingApi => "Core.Launcher.Preflight.Proof.Read.Startup",
        PreflightCheck.SubModuleClassMissing => "Core.Launcher.Preflight.Proof.Read.Class",
        PreflightCheck.SubModuleTypeLoad => "Core.Launcher.Preflight.Proof.Read.Types",
        PreflightCheck.MergeFirstFileMissing => "Core.Launcher.Preflight.Proof.Read.Merge",
        PreflightCheck.CampaignStartMissingApi => "Core.Launcher.Preflight.Proof.Read.Campaign",
        PreflightCheck.UnimplementedMember => "Core.Launcher.Preflight.Proof.Read.Unimplemented",
        PreflightCheck.InvalidDeclaredAssembly => "Core.Launcher.Preflight.Proof.Read.InvalidAssembly",
        PreflightCheck.XmlNodeIncomplete => "Core.Launcher.Preflight.Proof.Read.ManifestFault",
        PreflightCheck.SubModuleIncomplete => "Core.Launcher.Preflight.Proof.Read.SubModuleFault",
        PreflightCheck.NavalDlcVersionMismatch => "Core.Launcher.Preflight.Proof.Read.NavalDlcVersion",
        PreflightCheck.StylesheetInvalid => "Core.Launcher.Preflight.Proof.Read.Stylesheet",
        PreflightCheck.DuplicateManagedTypeName => "Core.Launcher.Preflight.Proof.Read.ManagedType",
        PreflightCheck.TextTableStaticInitializer => "Core.Launcher.Preflight.Proof.Read.TextTable",
        _ => throw new ArgumentOutOfRangeException(nameof(check), check, "This check is not an Error by reading, so it has no proof to state.")
    };

    public static PreflightFinding Clamp(PreflightFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var ceiling = Ceiling(finding.Check);

        // Critical is 0 and Advisory is 2, so a smaller value is a worse grade.
        return finding.Grade < ceiling ? finding with { Grade = ceiling } : finding;
    }

    public static PreflightGrade From(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => PreflightGrade.Critical,
        IssueSeverity.Warning => PreflightGrade.Warning,
        _ => PreflightGrade.Advisory
    };

    public static IssueSeverity ToSeverity(PreflightGrade grade) => grade switch
    {
        PreflightGrade.Critical => IssueSeverity.Error,
        PreflightGrade.Warning => IssueSeverity.Warning,
        _ => IssueSeverity.Information
    };
}
