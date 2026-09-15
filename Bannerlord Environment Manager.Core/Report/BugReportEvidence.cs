using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.DryRun;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Report;

// One library the community treats as infrastructure, named with the version that was installed for
// the runs described in the report. Harmony, ButterLib, UIExtenderEx and MCM are the four that get
// asked about first on every mod page, so they are worth stating without being asked.
public sealed record BugReportLibrary(string Name, string Version);

// One file the subject module ships, as it is on disk. Size and date are here because they are how an
// author tells which build of theirs this actually was when the version number did not move.
public sealed record BugReportAssembly(
    string FileName,
    long SizeBytes,
    DateTimeOffset? Written,
    string AssemblyVersion = "");

public sealed record BugReportEnvironment(
    string GameVersion,
    string PlatformFolder,
    string OperatingSystem,
    // How the game was started, in the words the user would use: "BLSE (Bannerlord.BLSE.Standalone.exe)"
    // or "the game executable directly". Empty when BEM did not start it and has no record of who did.
    string LaunchedVia,
    IReadOnlyList<BugReportLibrary> Libraries);

// The crash exactly as it was captured, never as it was reconstructed. Frames come off the parsed
// report's innermost node; a report that carried no stack trace arrives here with none, and the
// drafter leaves the whole exception out rather than inventing one.
public sealed record BugReportCrash(
    string TypeFullName,
    string Message,
    IReadOnlyList<string> Frames,
    // The BUTR enhanced stack trace, when the crash handler wrote one. It carries the parameter count
    // and IL offset that tell two identically printed overloads apart.
    EnhancedStacktrace? Enhanced = null);

// Everything the drafter is allowed to know. Nothing in here is gathered by the drafter itself: it is
// handed what BEM already read, so a report can never describe a launch or a file that BEM did not see.
public sealed record BugReportRequest(
    ModuleId Subject,
    IReadOnlyList<ModuleEntry> LoadOrder,
    BugReportEnvironment Environment,
    IReadOnlyList<BugReportAssembly> SubjectAssemblies,
    BugReportCrash? Crash = null,
    // The saved search. Null means no search ran, which the report says in as many words rather than
    // quietly omitting the part that would have been the proof.
    BisectionSnapshot? Bisection = null,
    // A method BEM read back out of the subject's own assembly. Evidence, shown whole, exactly as the
    // reference report shows it.
    DecompiledMethod? Decompiled = null,
    // A finding from reading the files on disk without running them. Subordinate by construction: the
    // drafter says in the report that it is static analysis and that the launches are the evidence.
    string StaticFinding = "",
    // What the user does to make it happen, in their own words. Used to say what crashes rather than
    // guessing at it.
    string ReproductionStep = "");
