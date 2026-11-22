using System.Diagnostics;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Modules.KnownIssues;
using BannerlordEnvironmentManager.Core.Safety;

namespace BannerlordEnvironmentManager.Core.Launcher;

public enum PreflightGrade
{
    // This will crash the game, definitively. Only a check PreflightGrades.Ceiling allows may give it, and
    // only for a condition proven from the game's source or a reproduced crash.
    Critical,

    // This may crash the game, or a mod may or will work in a way nobody intended. Unproven is a Warning.
    Warning,

    // Everything else: true, worth knowing, and nothing to worry about. It lives behind a disclosure,
    // never in front of the Launch button, and never counts toward a badge.
    Advisory
}

// What the user said to do about a preflight that found something. Cancel is theirs to choose and never
// BEM's: there is no fourth value where BEM refuses.
public enum PreflightChoice
{
    LaunchAnyway,
    FixAndLaunch,
    Cancel
}

public enum PreflightCheck
{
    LaunchHistory,
    LoadOrder,
    MissingDeclaredAssembly,
    HarmonyPatchTarget,

    // One mod's prefix skips a game method whose empty result another mod's postfix reads.
    SkippedResultClash,

    MalformedModuleXml,
    ShadowedAssembly,
    GameVersion,
    RigConflict,
    ModSafety,

    // How much of the game's own 4096-byte command line buffer this load order uses. The one check
    // here that is arithmetic rather than inference: over the limit the game is killed by the C
    // runtime two seconds in, before any managed code runs, with no crash report and no log.
    CommandLineLength,

    // A module's stylesheet that leaves a shared dataset with nothing in it. The game reads the merged
    // document, so this removes the game's own entries too, and the failure surfaces far away from the
    // module that caused it.
    DatasetEmptiedByStylesheet,

    // One of the handful of libraries most mods are built against, absent. A mod that declares one does
    // not run without it, so this is a reason the load order will not work rather than an opinion.
    MissingPrerequisite,

    // One SubModuleClassType named by more than one enabled module. The game constructs and
    // initializes that class once per module naming it, so a loader keeping static state is set up and
    // torn through twice.
    DuplicateSubModuleClass,

    // The same clash where one side is a mod carrying its own copy of an installed BUTR stack module.
    // There the remedy is to stop the mod declaring and carrying the copy, so the installed module is
    // the only one, rather than to disable the installed module.
    BundledStackCopy,

    // A type, method or field an enabled module's assembly uses that this install, with its enabled
    // dependencies, does not have. Read from metadata; nothing here says the reference runs.
    MissingGameApi,

    // A recent run's log shows "Collection was modified" thrown with a stack running through an enabled
    // module's own code: the mod changed a list while looping over it. A fact about a past run, not a prediction.
    CollectionModified,

    // A recent run's first-chance trace shows a mod's collection breaking inside the game's parallel party
    // tick: the mod writes shared data from several threads, which corrupts it until the game dies.
    ParallelTickCorruption,

    // A recent engine log shows a burst of item render requests (RenderRequestBurstScanner.BurstThreshold or
    // more in two seconds). A fact about a past run, not a prediction.
    RenderRequestBurst,

    // A module whose loaded build was made for another game line: it binds on that line's game assemblies
    // on this computer and not on this one, or its manifest requires that line. The one MissingGameApi
    // grade shown by default, because it is the shape that crashed real runs.
    BuiltForLine,

    // A content XML file of an enabled module that is not well-formed. The game merges the files its
    // SubModule.xml names while loading, and one that does not parse stops the load.
    MalformedXml,

    // A recent engine "[FTL] Crash Report: Exception occurred inside invoke" block whose stack runs through
    // an enabled module. A fact about a past run.
    EngineInvokeFailure,

    // Modules that were in several launches that crashed and in none that ended cleanly, from the launch
    // history. A with-and-without count, not a cause.
    CrashHistory,

    // A recent engine log says a module's shader cache does not match the game.
    StaleShaderCache,

    // A recent engine log says objects the game needs were never registered ("Null object reference found
    // with ID"), and a module's data of that kind failed schema validation or a stylesheet removes them.
    EngineDataLoad,

    // The last launches crashed and the history names no module. Saying so beats guessing one.
    CrashCauseUnknown,

    // A declared assembly that is on disk but is not a loadable .NET assembly.
    InvalidDeclaredAssembly,

    // Two or more enabled mods patch the same game method. A fact about the patches, not a verdict that
    // they conflict; it is the first thing to look at when the game dies inside that method.
    PatchOverlap,

    // A method Harmony runs while it applies a mod's patches (TargetMethod, TargetMethods, Prepare) uses a
    // type or member the game does not have, so applying the patches throws.
    PatchSetupMissingApi,

    // A method the game runs on every launch (the SubModule class's constructor, OnSubModuleLoad,
    // OnBeforeInitialModuleScreenSetAsRoot, and what those are certain to call) uses a type or member the
    // game does not have.
    StartupMissingApi,

    // The class SubModule.xml names is not in the file it names, or cannot be constructed without arguments.
    SubModuleClassMissing,

    // A type in a SubModule's file inherits from or implements a type the game does not have, so the
    // loader cannot list the file's types.
    SubModuleTypeLoad,

    // The first module in load order to register a merged dataset ships no file for it (a stylesheet
    // alone), so the game opens an empty path when a campaign starts.
    MergeFirstFileMissing,

    // A campaign behavior the mod constructs uses, on every path through its RegisterEvents, a type or
    // member the game does not have. The game calls RegisterEvents when the behavior is added to a
    // campaign, so a new campaign or a save load throws MissingMethodException (CalradianStart's
    // CampaignEvents.OnCharacterCreationIsOverEvent on v1.5.7).
    CampaignStartMissingApi,

    // A type in a mod's SubModule file leaves unimplemented a member that a game class it derives from or a
    // game interface it implements declares, so the runtime refuses to load that type and the loader's
    // Assembly.GetTypes throws (FamilyControl's log entries on v1.5.7).
    UnimplementedMember,

    // Windows Code Integrity (Smart App Control or a WDAC policy) refused to load a file of an enabled mod or
    // the game, read from the CodeIntegrity log, or Smart App Control is on or in evaluation and so may.
    SmartAppControl,

    // The engine log shows Harmony's own dependencies looked up in the game's main Gaming.Desktop bin folder
    // and not found (BLSE issue #158): Harmony never loads, whatever the mod list.
    XboxHarmonyDependency,

    // The Bannerlord Exception Manager is on, but Bannerlord.Harmony is not enabled, so nothing on this
    // launch can catch a crash: the Companion patches through Harmony and is left out without it.
    ExceptionManagerUnarmed,

    // A module's SubModule.xml has an <XmlNode> the game's loader cannot read (no <XmlName>, or an <XmlName>
    // with no id or path, or an <IncludedGameTypes> child with no value). The loader throws before any mod
    // code runs and the game does not start.
    XmlNodeIncomplete,

    // A module's <SubModule> lacks its Name, DLLName or SubModuleClassType, so the game's loader throws a
    // NullReferenceException on it and the boot ends.
    SubModuleIncomplete,

    // NavalDLC's RequiredBaseVersion is not the installed game's version, so the game shows a message and
    // exits (ModuleHelper.InitializeModules, Environment.Exit(0)).
    NavalDlcVersionMismatch,

    // A stylesheet a module registers for a merged dataset does not parse or compile, so the game's dataset
    // load throws (MBObjectManager.ApplyXslt) and the boot ends.
    StylesheetInvalid,

    // Two ManagedObject- or DotNetObject-derived types with one simple name in the enabled SubModule files:
    // the game registers such types by simple name and throws on the second, which stops the load.
    DuplicateManagedTypeName,

    // A mod Harmony-patches a game type whose static constructor reads GameTexts. The patch runs that
    // constructor while the module loads, before the text manager exists, and the runtime keeps the
    // failure, so the game crashes the first time it uses the type (Character Creation Next with
    // More Trait Info and Better Smithing Continued on v1.5.7).
    TextTableStaticInitializer
}

public sealed record FindingLink(string Label, string Url);

public sealed record PreflightFinding(
    PreflightGrade Grade,
    PreflightCheck Check,
    string Headline,
    string Detail,
    ModuleId? ModuleId = null,
    Func<ModuleEnvironment, ModuleEnvironment>? Fix = null,
    string FixLabel = "Fix",
    bool IsDestructive = false,
    // Set by Inspect from the request's accepted-risk keys, never by an AddXxx rule: a check has no
    // business knowing what the user already decided about its own output. Accepted findings stay in
    // Findings rather than being dropped, so they are never mistaken for a check that found nothing.
    bool Accepted = false,
    // A fix that changes files rather than the load order. Whoever runs it reads the modules again
    // afterwards, since the manifests it changed are part of what the load order was built from.
    Action? Remedy = null,
    // The key the same finding is accepted under on the Health pages, when it has one there.
    string? AlternateKey = null,
    // Hidden findings are still computed and counted, and shown only when the user asks for everything.
    // Graded by the 2026-10-30 deep dive: a class that fired on clean sessions and matched no crash is
    // Hidden, whatever its grade says about the worst case.
    FindingRelevance Relevance = FindingRelevance.Relevant,
    // The canonical, untranslated thing that failed (an API, a patch target, a file and line). Part of
    // the ignore key; absent, the headline stands in, which is the old behavior.
    string? Subject = null,
    // The class of finding for the ignore key when one check raises more than one class. Absent, the
    // check's own name is the class.
    string? ClassId = null,
    // Links the row shows beside its buttons, such as a Nexus Mods search for a mod whose folder is gone.
    IReadOnlyList<FindingLink>? Links = null)
{
    // Set by Inspect, never by an AddXxx rule: ClassId|ModuleId|ModuleVersion|Subject|GameLine, so an
    // "ignore always" stops applying when the module updates or the game moves to another minor line.
    public string? IgnoreKey { get; init; }

    // Turning the module off, offered beside a fix that keeps it on and never in place of one.
    // Never part of Fixable or Fix and launch: only the owner pressing its own button applies it.
    public Func<ModuleEnvironment, ModuleEnvironment>? TurnOff { get; init; }

    public string? TurnOffLabel { get; init; }

    // Set by Inspect when this exact finding was ignored before under another module version or game
    // line: the stored key it was ignored under, so a surface can say what changed.
    public string? PriorIgnoreKey { get; init; }

    // What changed since it was ignored, said in a sentence, or empty when it was never ignored.
    public string ChangedSinceIgnored =>
        FindingIgnoreKey.Parse(PriorIgnoreKey) is { } prior && FindingIgnoreKey.Parse(IgnoreKey) is { } now
            ? FindingIgnoreKey.DescribeChange(prior, now)
            : string.Empty;

    // Every other key the same finding may already be accepted under: the (Category, Heading) key a Health
    // page stored for its own copy of it, or the scoped key of the same module issue under another class.
    // Matched against both stores, so an acceptance made anywhere hides the finding here too.
    public IReadOnlyList<string> AlternateKeys { get; init; } = [];

    // For a Health heading that carries a count the launch check cannot reproduce.
    public System.Text.RegularExpressions.Regex? AlternatePattern { get; init; }

    // When a recorded launch crashed in a way this finding's own evidence predicts (a crash within two
    // minutes of its engine failure, a crash with the module enabled after its malformed file was written,
    // a crash with the module whose failing call a log names). Set by the detector; null when no run
    // proves it.
    public DateTimeOffset? CrashEvidenceUtc { get; init; }

    // Set by Inspect: the finding was accepted, and a crash that matches it happened after every
    // acceptance, so it is shown once more (findings catalog section 5). Accepting it again ends that.
    public bool ResurfacedAfterCrash { get; init; }

    // What a crash log has to name for the crash to be this finding's: "type:Full.Name" or
    // "member:Full.Name.Member". Empty for a finding no log line can be tied to.
    public IReadOnlyList<string> Faults { get; init; } = [];

    // Set by Inspect: this finding on this mod's files and this game version, the identity under which a
    // launch remembers carrying it (LaunchEvidence).
    public string? EvidenceKey { get; init; }

    // Set by Inspect: what made this an Error, or what stopped it being one.
    public FindingProof Proof { get; init; }

    public DateTimeOffset? ProofUtc { get; init; }

    public bool IsFixable => Fix is not null || Remedy is not null;

    public bool IsHiddenByDefault => Relevance == FindingRelevance.Hidden;

    // A hidden finding never gates a launch, whatever its grade.
    public bool IsProminent => !IsHiddenByDefault && Grade is PreflightGrade.Critical or PreflightGrade.Warning;

    public string GradeText => Grade switch
    {
        PreflightGrade.Critical => Strings.Current["Core.Launcher.Preflight.Grade.Critical"],
        PreflightGrade.Warning => Strings.Current["Core.Launcher.Preflight.Grade.Warning"],
        _ => Strings.Current["Core.Launcher.Preflight.Grade.Note"]
    } + (Accepted ? Strings.Current["Core.Launcher.Preflight.Grade.AcceptedSuffix"] : string.Empty);

    // The identity a mute has to key on. Not the module: a module that trades one broken finding for a
    // different one must not inherit an acceptance that was only ever about the first. Headline carries
    // the specific patch, type or id involved, so a changed wording is a changed key, and the safe
    // failure is the finding coming back rather than staying hidden.
    public static string KeyFor(PreflightCheck check, string headline) => $"{check}|{headline}";

    public string Key => KeyFor(Check, Headline);
}

// What BEM could not look at, as opposed to what it looked at and found nothing in. The two mean
// opposite things to whoever reads the verdict, and a preflight that says "nothing found" while half
// its checks never ran has overstated the result.
public sealed record PreflightNote(PreflightCheck Check, string Reason);

public sealed record PreflightReport(
    IReadOnlyList<PreflightFinding> Findings,
    IReadOnlyList<PreflightNote> NotChecked,
    string HistoryLine,
    int LoadOrderNoteCount,
    TimeSpan Elapsed)
{
    public static PreflightReport Empty { get; } = new([], [], string.Empty, 0, TimeSpan.Zero);

    // What still gates a launch. An accepted finding is prominent by grade and stays out of this list
    // by choice, not by disappearing from Findings: Prominent is "what to ask about", not "what is
    // true".
    public IReadOnlyList<PreflightFinding> Prominent =>
        [.. Findings.Where(f => f.IsProminent && !f.Accepted && !IgnoredOnce.Contains(f.Key))];

    // Findings the owner chose to pass this one launch. Nothing stores them: they stay in Findings, and
    // the next launch asks again.
    public IReadOnlySet<string> IgnoredOnce { get; init; } = new HashSet<string>();

    public IReadOnlyList<PreflightFinding> Advisories =>
        [.. Findings.Where(f => f.Grade == PreflightGrade.Advisory && !f.IsHiddenByDefault)];

    // Everything shown only on request: computed, honest, and kept out of every count above.
    public IReadOnlyList<PreflightFinding> Hidden =>
        [.. Findings.Where(f => f.IsHiddenByDefault)];

    // What a surface shows by default, in the order Findings holds them.
    public IReadOnlyList<PreflightFinding> Relevant =>
        [.. Findings.Where(f => !f.IsHiddenByDefault)];

    public int AcceptedCount => Findings.Count(f => f.IsProminent && f.Accepted);

    // Every check that could not run, in one line for a collapsed or hidden area (findings catalog K32:
    // seven unreadable assemblies were seven separate notes). The notes themselves stay in NotChecked for
    // whoever opens the detail. Empty when every check ran.
    public string NotCheckedSummary => NotChecked.Count == 0
        ? string.Empty
        : Strings.Current.Plural(
            "Core.Launcher.Preflight.NotChecked.Collapsed",
            NotChecked.Count,
            NotChecked.Select(n => n.Check).Distinct().Count(),
            string.Join(", ", NotChecked.Select(n => n.Check.ToString()).Distinct()));

    // The notes grouped by the check they belong to, for a detail view that lists each check once.
    public IReadOnlyList<IGrouping<PreflightCheck, PreflightNote>> NotCheckedByCheck =>
        [.. NotChecked.GroupBy(n => n.Check)];

    // Never true just because everything prominent was accepted. Accepting a risk moves it out of the
    // dialog that gates the next launch; it does not make the preflight's own account of the install
    // stop mentioning it.
    public bool IsClear => !Findings.Any(f => f.IsProminent);

    // Exactly what one press of Fix and launch would apply, and nothing else. Only the rows the dialog
    // actually showed, so the count on the button is the count of things the user read: an advisory
    // fix that reorders half the list is reachable from its own row and never from a batch. Destructive
    // fixes are out for the same reason Fix all leaves pruning to the Prune button, and an accepted
    // finding is out because accepting it was the user choosing not to fix it.
    public IReadOnlyList<PreflightFinding> Fixable =>
        [.. Findings.Where(f => f.IsProminent && f.IsFixable && !f.IsDestructive && !f.Accepted && !IgnoredOnce.Contains(f.Key))];

    // Never "your load order is fine". The strongest true statement about a clear preflight is that
    // the checks that ran found nothing that predicts a crash, and the checks that did not run are
    // named in the same breath.
    public string Summary
    {
        get
        {
            var head = IsClear
                ? Strings.Current["Core.Launcher.Preflight.Summary.Clear"]
                : Prominent.Count > 0
                    ? Strings.Current.Plural("Core.Launcher.Preflight.Summary.ThingsToRead", Prominent.Count)
                    : Strings.Current["Core.Launcher.Preflight.Summary.NothingLeftToRead"];

            var tail = new List<string>();

            if (AcceptedCount > 0)
                tail.Add(Strings.Current.Plural("Core.Launcher.Preflight.Summary.AcceptedTail", AcceptedCount));

            if (Advisories.Count > 0)
                tail.Add(Strings.Current.Plural("Core.Launcher.Preflight.Summary.NotesTail", Advisories.Count));

            if (LoadOrderNoteCount > 0)
            {
                tail.Add(Strings.Current.Plural(
                    "Core.Launcher.Preflight.Summary.LoadOrderNotesTail", LoadOrderNoteCount));
            }

            if (NotChecked.Count > 0)
                tail.Add(Strings.Current.Plural("Core.Launcher.Preflight.Summary.ChecksNotRunTail", NotChecked.Count));

            var body = tail.Count == 0 ? string.Empty : $" {string.Join(", ", tail)}.";

            return string.IsNullOrWhiteSpace(HistoryLine) ? head + body : $"{head} {HistoryLine}{body}";
        }
    }

    // No elapsed time here on purpose. Elapsed covers the checks and not the assembly index the caller
    // builds first, so putting it on screen would name a number that is not how long the user waited.
    //
    // The disclosure ships collapsed, so this line is the whole preflight for anyone who does not open
    // it. A check that could not run has to be counted here for the same reason Summary counts it:
    // "nothing stops a mod working" over a check that never ran is the clear verdict of a scan that
    // did not happen.
    public string Header
    {
        get
        {
            var head = IsClear
                ? Strings.Current.Plural("Core.Launcher.Preflight.Header.Clear", Advisories.Count)
                : Strings.Current.Plural("Core.Launcher.Preflight.Header.NotClear", Advisories.Count, Prominent.Count);

            var accepted = AcceptedCount == 0
                ? string.Empty
                : Strings.Current.Format("Core.Launcher.Preflight.Header.AcceptedSuffix", AcceptedCount);

            return NotChecked.Count == 0
                ? head + accepted
                : head + accepted
                  + Strings.Current.Plural("Core.Launcher.Preflight.Header.NotCheckedSuffix", NotChecked.Count);
        }
    }
}

// Assemblies and Safety are optional and arrive null when they were not available, which is a
// different statement from arriving empty. Neither gates anything: with both absent the preflight
// still runs every check that needs nothing but the load order and the game folder.
//
// Target is the launch this preflight is about, and it is what makes the command line measurable:
// the length that kills the game is the length of the arguments that particular target sends, which
// only the resolver can build. Absent, the check is named as one that did not run.
//
// WatchIsArmed only changes the wording: watching adds the companion's module id to the same list,
// so an order sitting on the limit is worth naming differently when BEM is about to add to it.
public sealed record PreflightRequest(
    ModuleEnvironment Environment,
    ModuleVersion GameVersion,
    AssemblyIndex? Assemblies = null,
    string? LoadedPlatformFolder = null,
    IReadOnlyList<LaunchRecord>? History = null,
    IReadOnlyList<SafetyScanResult>? Safety = null,
    LaunchTarget? Target = null,
    bool WatchIsArmed = false,
    // Supplied rather than recomputed: working out what a stylesheet did means running the whole merge,
    // which is far too slow to put in front of every launch. Absent means not examined, and is said so.
    IReadOnlyList<XmlTransformOutcome>? Transforms = null,
    // Needed only to look for the loader, which is not a module and so cannot be found in the load
    // order. Absent means the loader is not checked, which is said rather than assumed either way.
    string? GameInstallPath = null,
    // Keys the user has already accepted, from AcceptedRiskStore. Absent or empty behaves exactly like
    // no acceptances on record: every prominent finding gates the next launch, same as before this
    // existed.
    IReadOnlySet<string>? AcceptedRiskKeys = null,
    // Present when a mod's bundled copies of the BUTR stack should give way to the installed stack.
    // Absent, a bundler is treated like any other module declaring a class twice, and the fix offered
    // disables the installed stack module instead.
    BundledStackSetAsideStore? SetAsideStore = null,
    // The binding-failure lines (missing method, field or type) of the newest run logs, from
    // RecentRunErrors. A reference the metadata proves unresolvable that a recent run also threw on is
    // no longer a maybe. Absent behaves exactly like a run that logged none.
    string? RecentRunErrors = null,
    // The worst burst of "Render Requested" lines in a recent engine log, from RenderRequestBurstScanner.
    // Absent behaves exactly like a run that logged no burst.
    RenderRequestBurst? RecentRenderBurst = null,
    // Keys the owner accepted on the Health pages, from AcceptedFindingStore. Only a finding that has a
    // Health counterpart (a mod safety alarm) is matched against them, through its AlternateKey.
    // Absent behaves exactly like none on record.
    IReadOnlySet<string>? AcceptedFindingKeys = null,
    // The game assemblies of other game versions on this computer (other instances). With them, a module
    // whose loaded build binds only on another line is named as built for that line. Absent, only a
    // module whose manifest requires a newer line can be named that way.
    IReadOnlyList<GameBuildOnDisk>? OtherGameBuilds = null,
    // Where a proposed XML repair keeps the original bytes. Absent, a repairable file is still reported
    // with the exact edit, but no button applies it.
    string? XmlRepairBackupFolder = null,
    // Where a cleared shader cache is kept so it can be put back. Absent, a stale cache is still reported,
    // but no button clears it.
    ClearedFileStore? ClearedFiles = null,
    // When each accepted key was recorded, from AcceptedRiskStore.LoadRecordedTimes and
    // AcceptedFindingStore.LoadRecordedTimes. Only used to bring an accepted finding back once after a
    // later crash that matches it; absent, nothing accepted ever comes back.
    IReadOnlyDictionary<string, DateTimeOffset>? AcceptedAt = null,
    // Sends a folder to the Recycle Bin. Absent, a module whose SubModule.xml cannot be read is still
    // reported, but no button recycles it.
    Action<string>? RecycleFolder = null,
    // Module id to the file name of the archive that installed it, only where that file is still there.
    // Absent, a module with a broken assembly is reported with no way back to its archive offered.
    IReadOnlyDictionary<string, string>? RecordedArchives = null,
    // Takes the owner to that archive in Library. Installing stays Library's job, safety check included.
    Action<string>? ShowArchive = null,
    // Module id to other archives of the same Nexus mod in the archives folder: a build for another game
    // line may be among them. BEM names them; which one suits this game the owner reads off the file.
    IReadOnlyDictionary<string, IReadOnlyList<string>>? OtherArchives = null,
    // The companion's first-chance traces of recent runs, from ParallelTickCorruption.ReadRecent. Absent
    // behaves exactly like runs that threw nothing.
    IReadOnlyList<BannerlordEnvironmentManager.Core.DryRun.FirstChanceTrace>? RecentFirstChance = null,
    // The Smart App Control state and the recent CodeIntegrity refusals, from SmartAppControl.Read. Absent
    // behaves exactly like a machine where Windows refused nothing.
    WindowsCodeIntegrity? CodeIntegrity = null,
    // Whether the Bannerlord Exception Manager's one setting is on. Absent or false, its Harmony need is not
    // checked.
    bool ExceptionManagerOn = false);

// Everything BEM already computes that predicts a bad launch, asked once, at the moment it can still
// change the answer. It never blocks: the user decides, and the only job here is that they decide
// knowing what BEM knows.
//
// The grading is calibrated against a real 242-module install rather than against what is
// easy to detect. Rules that fire on a healthy install are graded Advisory and live behind a
// disclosure, because a preflight that argues with a working setup on every launch gets ignored, and
// then the one that matters is ignored with it.
public static class LaunchPreflight
{
    private const int NamesShown = 8;

    // Profiling hook: called with each check's name and wall time. Null in the app.
    public static Action<string, TimeSpan>? StepTimer { get; set; }

    public static PreflightReport Inspect(PreflightRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var findings = new List<PreflightFinding>();
        var notChecked = new List<PreflightNote>();

        var issues = LoadOrderValidator.Validate(request.Environment);
        // The two diagnoses that are never a row here: 31 of them on the owner's install, none tied to a
        // crash. Every other note is a row behind the disclosure, with its fix.
        static bool CountedOnly(LoadOrderIssue i) => i.Kind is IssueKind.MissingOptionalDependency or IssueKind.VersionMismatch;

        var noteCount = issues.Count(CountedOnly);

        // A load order carried over from another install lists every mod that is not here, which was 150 identical
        // notes on the owner's Steam install. It is one fact with one fix, so it reads as one note.
        var gone = issues
            .Where(i => i.Kind == IssueKind.OrphanEntry
                && i.Message == Strings.Current.Format("Core.LoadOrder.Validator.OrphanNoFolder", i.ModuleId))
            .ToList();
        var merged = gone.Count >= 2 ? gone.ToHashSet() : [];

        foreach (var issue in issues.Where(i => !CountedOnly(i) && !merged.Contains(i)))
            findings.Add(WithRecycle(FromLoadOrder(issue), issue, request));

        if (merged.Count > 0)
            findings.Add(FromMissingFolders(gone));

        T Step<T>(string name, Func<T> run)
        {
            var started = Stopwatch.GetTimestamp();
            var result = run();
            StepTimer?.Invoke(name, Stopwatch.GetElapsedTime(started));
            return result;
        }

        Step("AddMissingDeclaredAssemblies", () => { AddMissingDeclaredAssemblies(request, findings); return 0; });
        Step("AddMalformedXml", () => { AddMalformedXml(request, findings); return 0; });
        Step("AddManifestFaults", () => { AddManifestFaults(request, findings); return 0; });
        Step("AddSubModuleFaults", () => { AddSubModuleFaults(request, findings); return 0; });
        Step("AddDuplicateManagedTypes", () => { AddDuplicateManagedTypes(request, findings); return 0; });
        Step("AddNavalDlcVersion", () => { AddNavalDlcVersion(request, findings); return 0; });
        var patchSites = new Dictionary<string, PatchApplicationSite>(StringComparer.OrdinalIgnoreCase);
        var harmony = Step("AddHarmonyPatchTargets", () => AddHarmonyPatchTargets(request, findings, notChecked, patchSites));
        Step("AddPatchOverlaps", () => { AddPatchOverlaps(request, findings, notChecked); return 0; });
        var missingApis = Step("AddMissingGameApis", () => AddMissingGameApis(request, findings, notChecked));
        Step("AddPatchSetupMissingApis", () => { AddPatchSetupMissingApis(harmony, missingApis, findings, patchSites); return 0; });
        Step("AddStartupProofs", () => { AddStartupProofs(request, missingApis, findings); return 0; });
        Step("AddUnimplementedMembers", () => { AddUnimplementedMembers(request, missingApis, findings); return 0; });
        Step("MissingMembers.AddToPreflight", () => { MissingMembers.AddToPreflight(request, findings, missingApis); return 0; });
        Step("AddCollectionModified", () => { AddCollectionModified(request, findings); return 0; });
        Step("AddParallelTickCorruption", () => { AddParallelTickCorruption(request, findings); return 0; });
        Step("AddEngineInvokeFailures", () => { AddEngineInvokeFailures(request, findings); return 0; });
        Step("AddStaleShaderCaches", () => { AddStaleShaderCaches(request, findings); return 0; });
        Step("AddEngineDataLoadFailures", () => { AddEngineDataLoadFailures(request, findings); return 0; });
        Step("AddSmartAppControl", () => { AddSmartAppControl(request, findings); return 0; });
        Step("AddXboxHarmonyDependency", () => { AddXboxHarmonyDependency(request, findings); return 0; });
        Step("AddExceptionManagerUnarmed", () => { AddExceptionManagerUnarmed(request, findings); return 0; });
        Step("AddInvalidDeclaredAssemblies", () => { AddInvalidDeclaredAssemblies(request, findings); return 0; });
        Step("AddRenderRequestBurst", () => { AddRenderRequestBurst(request, findings); return 0; });
        Step("AddShadowedAssemblies", () => { AddShadowedAssemblies(request, findings, notChecked); return 0; });
        Step("AddGameVersionGaps", () => { AddGameVersionGaps(request, findings, notChecked, missingApis); return 0; });
        Step("AddRigConflicts", () => { AddRigConflicts(request, findings); return 0; });
        Step("AddSafety", () => { AddSafety(request, findings, notChecked); return 0; });
        Step("AddCommandLineLength", () => { AddCommandLineLength(request, findings, notChecked); return 0; });
        Step("AddEmptiedDatasets", () => { AddEmptiedDatasets(request, findings, notChecked); return 0; });
        Step("AddMissingPrerequisites", () => { AddMissingPrerequisites(request, findings); return 0; });
        Step("AddDuplicateSubModuleClasses", () => { AddDuplicateSubModuleClasses(request, findings); return 0; });
        Step("AddMergeFirstFileMissing", () => { AddMergeFirstFileMissing(request, findings); return 0; });
        Step("AddInvalidStylesheets", () => { AddInvalidStylesheets(request, findings); return 0; });
        Step("AddTextTableStaticInitializers", () => { AddTextTableStaticInitializers(request, findings, notChecked); return 0; });

        var history = Step("AddHistory", () => AddHistory(request, findings));
        Step("AddCrashHistory", () => { AddCrashHistory(request, findings); return 0; });

        // Whatever a check above asked for, no finding leaves here graded worse than its kind is proven to
        // be. The table is the single statement of what may say "this will crash the game".
        for (var i = 0; i < findings.Count; i++)
            findings[i] = PreflightGrades.Clamp(findings[i]);

        // Then what was watched, which outranks what was read in both directions.
        ApplyEvidence(request, findings);

        AssignIgnoreKeys(request, findings);
        ApplyAcceptedRisks(request, findings);

        stopwatch.Stop();

        return new PreflightReport(
            [.. InDisplayOrder(findings)],
            notChecked,
            history,
            noteCount,
            stopwatch.Elapsed);
    }

    // Worst first. "May cause problems" reads module by module (findings catalog section 7), so all that
    // is said of one mod sits together; findings no single mod owns come first in that group.
    public static IEnumerable<PreflightFinding> InDisplayOrder(IEnumerable<PreflightFinding> findings) =>
        findings
            .OrderBy(f => f.Grade)
            .ThenBy(f => f.Grade == PreflightGrade.Warning ? f.ModuleId?.Value ?? string.Empty : string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Check);

    // Marking rather than filtering: every rule above still runs and still finds exactly what it would
    // have found with no acceptances on record, so Findings stays the complete, honest account of the
    // install. Only Prominent, the list a launch dialog reads, looks at Accepted at all.
    //
    // Every grade can be accepted, Critical included: the owner decides what BEM may keep telling him,
    // and a finding he has accepted is never raised again. The finding still exists in Findings.
    //
    // A scoped key (IgnoreKey) is matched first. A legacy key (Check|Headline, written before scoped keys
    // existed) still matches, so nothing the owner accepted comes back on upgrade; AcceptedRiskStore's
    // UpgradeLegacy rewrites those to scoped keys once the finding is seen again.
    private static void ApplyAcceptedRisks(PreflightRequest request, List<PreflightFinding> findings)
    {
        var accepted = request.AcceptedRiskKeys;
        var acceptedOnHealth = request.AcceptedFindingKeys;

        var everywhere = new HashSet<string>(accepted ?? new HashSet<string>(), StringComparer.Ordinal);
        everywhere.UnionWith(acceptedOnHealth ?? new HashSet<string>());

        var scoped = everywhere
            .Select(FindingIgnoreKey.Parse)
            .OfType<FindingIgnoreKey>()
            .ToList();

        for (var i = 0; i < findings.Count; i++)
        {
            var finding = findings[i];

            // Keys from the two stores live in different namespaces (a check name against a Health category),
            // so one set holding both cannot match a finding by accident.
            var matched = MatchedKeys(finding, accepted, acceptedOnHealth, everywhere);

            if (matched.Count > 0)
            {
                // Findings catalog section 5: a finding a run proves is shown once more after "ignore
                // always", on the first crash that matches it. Once is enough: accepting it again records
                // a time after that crash, and the same crash never brings it back.
                findings[i] = ResurfacesAfterCrash(finding, matched, request.AcceptedAt)
                    ? finding with { ResurfacedAfterCrash = true }
                    : finding with { Accepted = true };
                continue;
            }

            if (FindingIgnoreKey.Parse(finding.IgnoreKey) is { } current
                && scoped.FirstOrDefault(old => old.SameFindingAs(current)) is { } prior)
            {
                findings[i] = finding with { PriorIgnoreKey = prior.ToString() };
            }
        }
    }

    private static List<string> MatchedKeys(
        PreflightFinding finding,
        IReadOnlySet<string>? accepted,
        IReadOnlySet<string>? acceptedOnHealth,
        HashSet<string> everywhere)
    {
        var matched = new List<string>();

        if (finding.IgnoreKey is { } ignoreKey && everywhere.Contains(ignoreKey))
            matched.Add(ignoreKey);

        if (accepted?.Contains(finding.Key) ?? false)
            matched.Add(finding.Key);

        if (finding.AlternateKey is { } alternate && everywhere.Contains(alternate))
            matched.Add(alternate);

        matched.AddRange(finding.AlternateKeys.Where(everywhere.Contains));

        if (finding.AlternatePattern is { } pattern && acceptedOnHealth is not null)
            matched.AddRange(acceptedOnHealth.Where(key => pattern.IsMatch(key)));

        return matched;
    }

    // True when the run evidence (a crash that followed the finding's own evidence) is newer than every
    // acceptance that matched. An acceptance whose time is not known is taken as covering it, so nothing
    // comes back on a guess.
    private static bool ResurfacesAfterCrash(
        PreflightFinding finding, IReadOnlyList<string> matched, IReadOnlyDictionary<string, DateTimeOffset>? acceptedAt)
    {
        if (finding.CrashEvidenceUtc is not { } crash || acceptedAt is null)
            return false;

        foreach (var key in matched)
        {
            if (!acceptedAt.TryGetValue(key, out var when) || when >= crash)
                return false;
        }

        return true;
    }

    // The end of the newest launch that crashed with this module enabled, when it ended after a given time.
    private static DateTimeOffset? LatestCrashWith(IReadOnlyList<LaunchRecord>? history, ModuleId module, DateTimeOffset? after = null)
    {
        if (history is null)
            return null;

        DateTimeOffset? latest = null;

        foreach (var record in history)
        {
            if (record.Outcome != LaunchOutcome.Crashed || record.EndedUtc is not { } ended)
                continue;

            if (!record.Order.Entries.Any(e => e.IsEnabled && string.Equals(e.Id, module.Value, StringComparison.OrdinalIgnoreCase)))
                continue;

            var end = new DateTimeOffset(DateTime.SpecifyKind(ended, DateTimeKind.Utc));

            if ((after is null || end > after) && (latest is null || end > latest))
                latest = end;
        }

        return latest;
    }

    private static void AssignIgnoreKeys(PreflightRequest request, List<PreflightFinding> findings)
    {
        var versions = new Dictionary<ModuleId, string>();

        foreach (var entry in request.Environment.Entries)
            versions.TryAdd(entry.Id, entry.Manifest?.VersionText is { Length: > 0 } text ? text : entry.Version.ToString());

        for (var i = 0; i < findings.Count; i++)
        {
            var finding = findings[i];
            var version = finding.ModuleId is { } id && versions.TryGetValue(id, out var v) ? v : string.Empty;

            findings[i] = finding with
            {
                IgnoreKey = FindingIgnoreKey.For(
                    finding.ClassId ?? finding.Check.ToString(),
                    finding.ModuleId,
                    version,
                    finding.Subject ?? finding.Headline,
                    request.GameVersion).ToString(),

                // The built-for-line finding is the same module issue the Health page lists as unresolved
                // game APIs, so an acceptance of that one covers this one.
                AlternateKeys = finding.Check == PreflightCheck.BuiltForLine
                    ? [.. finding.AlternateKeys,
                        FindingKeys.Scoped(PreflightCheck.MissingGameApi, finding.ModuleId, version, FindingKeys.WholeModule, request.GameVersion)]
                    : finding.AlternateKeys
            };
        }
    }

    // An Error from the validator is a module that does not work: a hard dependency missing, two
    // modules that declare each other incompatible, a loop no order satisfies. An unreadable manifest
    // and a duplicated id are the two warnings the game itself acts on, by skipping the module and by
    // picking one folder. Everything else the validator calls a warning is about where a module sits
    // relative to another, which nothing in the engine enforces, so it is advice.
    // A module the game skips because its SubModule.xml cannot be read is repaired by removing it, so
    // its fix sends the folder to the Recycle Bin, where it can be restored. Accepting the risk was the
    // only choice before, which left a broken folder in Modules for good.
    private static PreflightFinding WithRecycle(PreflightFinding finding, LoadOrderIssue issue, PreflightRequest request)
    {
        if (issue.Kind != IssueKind.UnreadableManifest || request.RecycleFolder is not { } recycle)
            return finding;

        var folder = request.Environment.Unreadable
            .FirstOrDefault(unreadable => unreadable.FolderName.Equals(issue.ModuleId.Value, StringComparison.OrdinalIgnoreCase))
            ?.FolderPath;

        return folder is null
            ? finding
            : finding with
            {
                Fix = null,
                FixLabel = Strings.Current["Core.Launcher.Preflight.LoadOrder.FixLabel.RecycleModule"],
                IsDestructive = true,
                Remedy = () => recycle(folder)
            };
    }

    // A mod whose folder is gone is often a Steam Workshop item its author removed, so Steam deleted the files.
    private static FindingLink NexusSearchLink(ModuleId id) => new(
        Strings.Current.Format("Core.Launcher.Preflight.LoadOrder.MissingFolders.FindOnNexus", id.Value),
        "https://www.nexusmods.com/games/mountandblade2bannerlord/mods?keyword=" + Uri.EscapeDataString(id.Value));

    private static PreflightFinding FromMissingFolders(IReadOnlyList<LoadOrderIssue> gone)
    {
        var names = string.Join(", ", gone.Take(NamesShown).Select(issue => issue.ModuleId.Value));

        return new PreflightFinding(
            PreflightGrade.Advisory,
            PreflightCheck.LoadOrder,
            Strings.Current.Format("Core.Launcher.Preflight.LoadOrder.MissingFolders.Headline", gone.Count),
            Strings.Current.Format("Core.Launcher.Preflight.LoadOrder.MissingFolders.Detail", names),
            Fix: environment => gone.Aggregate(environment, (current, issue) => issue.Fix!(current)),
            FixLabel: FixLabelFor(IssueKind.OrphanEntry),
            IsDestructive: true,
            Subject: "OrphanEntry:missing-folders",
            Links: gone.Take(NamesShown).Select(issue => NexusSearchLink(issue.ModuleId)).ToList());
    }

    private static PreflightFinding FromLoadOrder(LoadOrderIssue issue) => FromLoadOrderIssue(issue) with
    {
        Links = issue.Kind == IssueKind.OrphanEntry
            && issue.Message == Strings.Current.Format("Core.LoadOrder.Validator.OrphanNoFolder", issue.ModuleId)
                ? [NexusSearchLink(issue.ModuleId)]
                : null,
        AlternateKeys = issue.Kind == IssueKind.DuplicateModuleId ? [FindingKeys.LegacyDuplicateId(issue.ModuleId)] : []
    };

    private static PreflightFinding FromLoadOrderIssue(LoadOrderIssue issue) => new(
        PreflightGrades.From(issue.Severity),
        PreflightCheck.LoadOrder,
        issue.Message,
        DetailFor(issue),
        issue.ModuleId,
        issue.Fix,
        FixLabelFor(issue.Kind),
        issue.IsDestructive,
        Subject: issue.Kind == IssueKind.DuplicateModuleId
            ? FindingKeys.DuplicateIdSubject(issue.ModuleId)
            : $"{issue.Kind}:{issue.TargetId?.Value ?? issue.ModuleId.Value}");

    private static string DetailFor(LoadOrderIssue issue) => issue.Kind switch
    {
        IssueKind.MissingDependency =>
            Strings.Current["Core.Launcher.Preflight.LoadOrder.Detail.MissingDependency"],
        IssueKind.DisabledDependency =>
            Strings.Current["Core.Launcher.Preflight.LoadOrder.Detail.DisabledDependency"],
        IssueKind.Incompatible =>
            Strings.Current["Core.Launcher.Preflight.LoadOrder.Detail.Incompatible"],
        IssueKind.CyclicDependency =>
            Strings.Current["Core.Launcher.Preflight.LoadOrder.Detail.CyclicDependency"],
        IssueKind.OrderViolation =>
            Strings.Current["Core.Launcher.Preflight.LoadOrder.Detail.OrderViolation"],
        IssueKind.UnreadableManifest =>
            Strings.Current["Core.Launcher.Preflight.LoadOrder.Detail.UnreadableManifest"],
        IssueKind.DuplicateModuleId =>
            Strings.Current["Core.Launcher.Preflight.LoadOrder.Detail.DuplicateModuleId"],
        IssueKind.OrphanEntry =>
            Strings.Current["Core.Launcher.Preflight.LoadOrder.Detail.OrphanEntry"],
        IssueKind.UnverifiedOfficialClaim =>
            Strings.Current["Core.Launcher.Preflight.LoadOrder.Detail.UnverifiedOfficialClaim"],
        // The module declares no relationship to Native, so there is no constraint for a sort to move it
        // by and BEM offers no fix here: dragging it is the only thing that moves it.
        IssueKind.InfrastructureAfterNative or IssueKind.ContentBeforeNative =>
            Strings.Current["Core.Launcher.Preflight.LoadOrder.Detail.RigRelative"],
        _ =>
            Strings.Current["Core.Launcher.Preflight.LoadOrder.Detail.Default"]
    };

    private static string FixLabelFor(IssueKind kind) => kind switch
    {
        IssueKind.DisabledDependency => Strings.Current["Core.Launcher.Preflight.LoadOrder.FixLabel.TurnDependencyOn"],
        IssueKind.OrphanEntry => Strings.Current["Core.Launcher.Preflight.LoadOrder.FixLabel.RemoveEntry"],
        _ => Strings.Current["Core.Launcher.Preflight.LoadOrder.FixLabel.PutInOrder"]
    };

    // The one check here that is not a prediction. The others say a run is likely to go wrong; this one
    // says the game stops with an error window before the main menu, because the engine opens a
    // declared assembly by path and there is nothing at that path. Critical is not a grading judgment,
    // it is what the loader does.
    //
    // It fires zero times on a healthy 230-module install, which is the bar every rule here
    // has to clear. The vanilla dedicated-server and console assemblies that are legitimately absent
    // carry the tags that say so and are ruled out by MissingDeclaredAssemblies rather than by a list
    // of names BEM would have to keep current.
    //
    // Disproof condition: an install where this names a file whose absence does not stop the game.
    private static void AddMissingDeclaredAssemblies(PreflightRequest request, List<PreflightFinding> findings)
    {
        foreach (var missing in MissingDeclaredAssemblies.Find(
            request.Environment.Entries, request.LoadedPlatformFolder, request.GameInstallPath))
        {
            findings.Add(new PreflightFinding(
                PreflightGrade.Warning,
                PreflightCheck.MissingDeclaredAssembly,
                missing.Describe(),
                Strings.Current["Core.Launcher.Preflight.MissingAssembly.Detail"],
                missing.ModuleId,
                Subject: string.Join(",", missing.FileNames)));
        }
    }

    // Critical: TaleWorlds.ObjectSystem XmlResource.cs GetXmlListAndApply throws on an <XmlNode> with no
    // <XmlName>, an <XmlName> with no id or path, or an <IncludedGameTypes> child with no value, and
    // TaleWorlds.MountAndBlade Module.cs LoadSubModules shows the "XML Error in module" box and rethrows
    // out of the module loop, so the boot dies before any mod code runs. Nothing here is repairable by BEM:
    // the module's author has to fix the file, so the one remedy offered is turning the module off.
    private static void AddManifestFaults(PreflightRequest request, List<PreflightFinding> findings)
    {
        foreach (var fault in ModuleXmlStructure.Inspect(request.Environment.Entries))
        {
            var id = fault.ModuleId;

            findings.Add(new PreflightFinding(
                PreflightGrade.Critical,
                PreflightCheck.XmlNodeIncomplete,
                Strings.Current.Format("Core.Launcher.Preflight.ManifestFault.Headline", fault.DisplayName, fault.Describe()),
                Strings.Current.Format("Core.Launcher.Preflight.ManifestFault.Detail", fault.Line, fault.Element),
                id,
                Subject: $"manifest:{fault.Kind}:{fault.Line}")
            {
                TurnOff = environment => Disable(environment, id),
                TurnOffLabel = Strings.Current.Format("Core.Launcher.Preflight.BuiltForLine.FixLabel", fault.DisplayName)
            });
        }
    }

    // Critical: TaleWorlds.ModuleManager ModuleHelper.cs InitializeModules exits the process when NavalDLC's
    // RequiredBaseVersion differs from the game's version. BEM cannot make the two match, so no remedy is
    // offered; the detail says to use the DLC copy that belongs to this game version.
    private static void AddNavalDlcVersion(PreflightRequest request, List<PreflightFinding> findings)
    {
        var platform = request.LoadedPlatformFolder ?? BannerlordEnvironmentManager.Core.Game.GameInstallLocator.StandardBinaryFolder;

        foreach (var mismatch in NavalDlcVersion.Find(request.Environment.Entries, request.GameInstallPath, platform))
        {
            findings.Add(new PreflightFinding(
                PreflightGrade.Critical,
                PreflightCheck.NavalDlcVersionMismatch,
                Strings.Current.Format(
                    "Core.Launcher.Preflight.NavalDlcVersion.Headline", mismatch.DisplayName, mismatch.Required.ToString(), mismatch.Game.ToString()),
                Strings.Current["Core.Launcher.Preflight.NavalDlcVersion.Detail"],
                mismatch.ModuleId,
                Subject: $"naval-dlc:{mismatch.Required}:{mismatch.Game}"));
        }
    }

    // Critical: TaleWorlds.MountAndBlade Module.cs CollectModuleAssemblyTypes does types.Add(type.Name, type)
    // for every ManagedObject or DotNetObject type of a SubModule file (a second one of the name throws and
    // the result is a CriticalError), and TaleWorlds.DotNet Managed.cs AddTypes unions each file's types
    // into the registered ones and calls ToDictionary, which throws on a name an earlier file registered,
    // with no catch around it in Module.AddSubModule. Either way the game does not finish loading.
    private static void AddDuplicateManagedTypes(PreflightRequest request, List<PreflightFinding> findings)
    {
        var platform = request.LoadedPlatformFolder ?? BannerlordEnvironmentManager.Core.Game.GameInstallLocator.StandardBinaryFolder;

        foreach (var duplicate in DuplicateManagedTypeNames.Find(request.Environment.Entries, request.GameInstallPath, platform))
        {
            var culprit = duplicate.Culprit!;
            var id = culprit.Module;

            findings.Add(new PreflightFinding(
                PreflightGrade.Critical,
                PreflightCheck.DuplicateManagedTypeName,
                Strings.Current.Format("Core.Launcher.Preflight.ManagedType.Headline", culprit.DisplayName, duplicate.SimpleName),
                Strings.Current.Format("Core.Launcher.Preflight.ManagedType.Detail", DuplicateManagedTypeNames.Describe(duplicate)),
                id,
                Subject: "managedtype:" + duplicate.SimpleName)
            {
                TurnOff = environment => Disable(environment, id),
                TurnOffLabel = Strings.Current.Format("Core.Launcher.Preflight.BuiltForLine.FixLabel", culprit.DisplayName)
            });
        }
    }

    // Critical: TaleWorlds.ModuleManager SubModuleInfo.cs LoadFrom throws on a <SubModule> with no Name,
    // DLLName or SubModuleClassType (or one without a value attribute), ModuleInfo.cs swallows it and keeps
    // the half-built entry, and TaleWorlds.MountAndBlade Module.cs LoadSubModules then hits its null DLLName
    // or null Assemblies outside any try. The module's author has to fix the file; turning it off clears it.
    private static void AddSubModuleFaults(PreflightRequest request, List<PreflightFinding> findings)
    {
        foreach (var fault in ModuleXmlStructure.InspectSubModules(request.Environment.Entries))
        {
            var id = fault.ModuleId;

            findings.Add(new PreflightFinding(
                PreflightGrade.Critical,
                PreflightCheck.SubModuleIncomplete,
                Strings.Current.Format("Core.Launcher.Preflight.ManifestFault.Headline", fault.DisplayName, fault.Describe()),
                Strings.Current.Format("Core.Launcher.Preflight.SubModuleFault.Detail", fault.Line, fault.Element),
                id,
                Subject: $"submodule:{fault.Kind}:{fault.Line}")
            {
                TurnOff = environment => Disable(environment, id),
                TurnOffLabel = Strings.Current.Format("Core.Launcher.Preflight.BuiltForLine.FixLabel", fault.DisplayName)
            });
        }
    }

    // Critical when the game reads the file (its SubModule.xml names it): the engine throws while merging
    // it and the boot dies, as it did in 11 of 11 launches with the BetterFemaleBody skins. A file nothing
    // names may never be read, so it is a hidden note. A repair is offered only for the one proven shape
    // and only as its own button, never as part of Fix and launch.
    private static void AddMalformedXml(PreflightRequest request, List<PreflightFinding> findings)
    {
        foreach (var bad in ModuleXmlWellFormedness.Inspect(request.Environment.Entries))
        {
            var detail = Strings.Current.Format(
                bad.IsReferenced
                    ? "Core.Launcher.Preflight.MalformedXml.Detail.Referenced"
                    : "Core.Launcher.Preflight.MalformedXml.Detail.Unreferenced",
                bad.Message);

            detail += bad.Repair is { } proposed
                ? Strings.Current.Format("Core.Launcher.Preflight.MalformedXml.RepairOffer", proposed.Describe())
                : Strings.Current["Core.Launcher.Preflight.MalformedXml.NoSafeRepair"];

            var backup = request.XmlRepairBackupFolder;
            var repair = bad.Repair;

            findings.Add(new PreflightFinding(
                bad.IsReferenced ? PreflightGrade.Warning : PreflightGrade.Advisory,
                PreflightCheck.MalformedXml,
                Strings.Current.Format(
                    "Core.Launcher.Preflight.MalformedXml.Headline", bad.ModuleName, bad.RelativePath, bad.Line, bad.Column),
                detail,
                bad.ModuleId,
                FixLabel: Strings.Current.Format("Core.Launcher.Preflight.MalformedXml.FixLabel", Path.GetFileName(bad.FilePath)),
                IsDestructive: true,
                Remedy: repair is not null && !string.IsNullOrWhiteSpace(backup) ? () => repair.Apply(backup) : null,
                Relevance: bad.IsReferenced ? FindingRelevance.Relevant : FindingRelevance.Hidden,
                Subject: $"{bad.RelativePath.Replace(Path.DirectorySeparatorChar, '/')}:{bad.Line}")
            {
                CrashEvidenceUtc = bad.IsReferenced ? LatestCrashWith(request.History, bad.ModuleId, WrittenUtc(bad.FilePath)) : null
            });
        }
    }

    private static DateTimeOffset? WrittenUtc(string path)
    {
        try
        {
            return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The healthy case is the copy that binds being the newest one installed, which is what the load
    // order already produces when the libraries everything depends on sit at the top. On a real
    // install six of the seven shadowed assemblies are exactly that, so they are not reported here at
    // all: the Overlaps page lists every copy of every one of them, and repeating that list in front
    // of the Launch button would bury the one row that is not the healthy case.
    //
    // What is left is a module shipping a copy NEWER than the one that binds, which is the direction
    // that breaks: whatever that module calls that arrived after the bound version is not there, and
    // MissingMethodException is what the game does about it.
    //
    // Disproof condition for the split: find a shadowed copy newer only in its build number whose
    // module genuinely calls an API the bound copy lacks. A library that adds public API in a build
    // increment would make this grading wrong, and the row would have to move up.
    // Harmony throws while it is processing a patch class whose target is not installed, and that
    // happens inside the declaring module's own load. On this install it surfaced as
    // "Could not load type 'TaleWorlds.MountAndBlade.MissionAgentSpawnLogic'" and the game never
    // reached the main menu, so it is graded exactly like a declared assembly that is not there.
    //
    // Calibration, measured rather than assumed: 236 enabled modules on a real install produce zero of
    // these. Enabling the one module that ships a patch against a type this game version does not
    // have produces exactly one, and that is the launch that died. Silent on a healthy install and
    // loud on the broken one is the shape a Critical grade has to have.
    //
    // The split by verdict came from correcting the first version of this rule, which
    // graded every finding Critical. A missing TYPE is the case that was observed killing a boot:
    // nothing declares it, so the load throws before the menu. A missing MEMBER on a type that does
    // resolve is a different animal. Alive Scenes patches ten members of SandBoxMissionViews, nine
    // of which exist, and it has been played with that module enabled. Harmony still throws, so the patch
    // does not apply and the ones the module declares after it may not either, but the run
    // continues. Grading that Critical would put a row in front of the Launch button on a setup its
    // owner considers healthy, which is how a preflight teaches you to ignore it.
    //
    // Disproof condition: a missing member that is observed stopping a boot. That would mean the
    // module's PatchAll is unguarded in a way BEM cannot see, and the split would have to go.
    //
    // The targets that cannot be answered without running the game are deliberately not reported.
    // There are 218 of them here on a setup that launches fine, so a note about them would be
    // non-empty on every healthy launch, and a preflight that always has something to say about
    // nothing trains the user to skip the one that matters.
    private static HarmonyPatchTargetReport? AddHarmonyPatchTargets(
        PreflightRequest request, List<PreflightFinding> findings, List<PreflightNote> notChecked,
        Dictionary<string, PatchApplicationSite> patchSites)
    {
        if (request.Assemblies is null)
        {
            notChecked.Add(new PreflightNote(
                PreflightCheck.HarmonyPatchTarget,
                Strings.Current["Core.Launcher.Preflight.Harmony.NotIndexed"]));
            return null;
        }

        if (request.Assemblies.Failed)
        {
            notChecked.Add(new PreflightNote(
                PreflightCheck.HarmonyPatchTarget,
                Strings.Current.Format("Core.Launcher.Preflight.Harmony.CouldNotRead", request.Assemblies.Error)));
            return null;
        }

        var report = HarmonyPatchTargets.Inspect(
            request.Assemblies, request.Environment.Entries,
            request.LoadedPlatformFolder ?? BannerlordEnvironmentManager.Core.Game.GameInstallLocator.StandardBinaryFolder);

        foreach (var problem in report.Problems)
        {
            notChecked.Add(new PreflightNote(
                PreflightCheck.HarmonyPatchTarget,
                $"{problem.Path}: {problem.Reason}"));
        }

        foreach (var target in report.Findings)
        {
            var site = SiteOf(patchSites, target.AssemblyPath);
            var (grade, relevance) = GradeHarmonyTarget(target, request.RecentRunErrors, site);

            findings.Add(new PreflightFinding(
                grade,
                PreflightCheck.HarmonyPatchTarget,
                target.Describe(),
                !IsMissingType(target)
                    ? grade == PreflightGrade.Critical
                        ? Strings.Current["Core.Launcher.Preflight.Harmony.Detail.MemberMissing.Certain"]
                        : Strings.Current["Core.Launcher.Preflight.Harmony.Detail.MemberMissing"]
                    : grade == PreflightGrade.Critical
                        ? Strings.Current["Core.Launcher.Preflight.Harmony.Detail.TypeMissing"]
                        : Strings.Current["Core.Launcher.Preflight.Harmony.Detail.TypeMissing.Unproven"],
                target.ModuleId,
                Relevance: relevance,
                Subject: FindingKeys.HarmonySubject(target)) { AlternateKeys = [FindingKeys.LegacyHarmony(target)] });
        }

        return report;
    }

    private static PatchApplicationSite SiteOf(Dictionary<string, PatchApplicationSite> patchSites, string assemblyPath)
    {
        if (!patchSites.TryGetValue(assemblyPath, out var site))
            patchSites[assemblyPath] = site = PatchApplicationGuard.Inspect(assemblyPath);

        return site;
    }

    // The crash BEM did not see coming. Harmony calls a patch class's TargetMethod, TargetMethods and
    // Prepare itself while it applies the patches, so a type or member one of them uses that the game does
    // not have throws right there, at startup, on every launch: Surrender Tweaks named
    // BanditsCampaignBehavior, which 1.4.8 does not have, and the owner's game died before the menu.
    // The target of such a patch is computed, so the target check above has nothing to say about it, and
    // the missing reference was a hidden row among dozens the install plays through.
    //
    // Critical only where the assembly is proven to apply its patches with nothing catching the failure.
    // Where the mod catches it the patch is lost and the game goes on, which is a Warning.
    public static void AddPatchSetupMissingApis(
        HarmonyPatchTargetReport? harmony, MissingApiReport? missingApis, List<PreflightFinding> findings,
        Dictionary<string, PatchApplicationSite> patchSites)
    {
        if (harmony is null || missingApis is null)
            return;

        // One row for a mod, naming the first patch that fails: the fix is the same for every one of them.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in harmony.Undecidable.Where(t => t.Verdict == HarmonyTargetVerdict.Computed && t.Category is null))
        {
            var reference = missingApis.Findings.FirstOrDefault(r =>
                string.Equals(r.AssemblyPath, target.AssemblyPath, StringComparison.OrdinalIgnoreCase)
                && r.UsedBy.Any(method => RunsWhilePatching(method, target.PatchClass)));

            if (reference is null || !seen.Add(target.AssemblyPath))
                continue;

            var site = SiteOf(patchSites, target.AssemblyPath);
            var certain = site == PatchApplicationSite.UncaughtInSubModule;
            var id = target.ModuleId;

            findings.Add(new PreflightFinding(
                certain ? PreflightGrade.Critical : PreflightGrade.Warning,
                PreflightCheck.PatchSetupMissingApi,
                Strings.Current.Format(
                    "Core.Launcher.Preflight.PatchSetup.Headline", target.ModuleName, ShortName(target.PatchClass), reference.Api),
                Strings.Current[certain
                    ? "Core.Launcher.Preflight.PatchSetup.Detail.Uncaught"
                    : site == PatchApplicationSite.Caught
                        ? "Core.Launcher.Preflight.PatchSetup.Detail.Caught"
                        : "Core.Launcher.Preflight.PatchSetup.Detail.Unknown"],
                id,
                environment => Disable(environment, id),
                Strings.Current.Format("Core.Launcher.Preflight.BuiltForLine.FixLabel", target.ModuleName),
                Subject: "setup:" + target.PatchClass) { Faults = [LaunchEvidence.FaultOf(reference)] });
        }
    }

    // What the game is certain to run in each enabled mod's SubModule file, set against what the install
    // does not have. Each row here is a crash on every launch, proven from the loader's own code, so each
    // is Critical and says which proof it rests on.
    public static void AddStartupProofs(PreflightRequest request, MissingApiReport? missingApis, List<PreflightFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(findings);

        var platform = request.LoadedPlatformFolder ?? BannerlordEnvironmentManager.Core.Game.GameInstallLocator.StandardBinaryFolder;

        foreach (var entry in request.Environment.Entries)
        {
            if (!entry.IsEnabled || entry.IsOfficial || entry.Manifest is not { } manifest
                || manifest.DeclaredAssemblies is not { } declared || string.IsNullOrWhiteSpace(manifest.FolderPath))
            {
                continue;
            }

            var id = entry.Id;
            var missingApi = false;

            foreach (var assembly in declared.Where(d => d.SubModuleClass is not null && !d.IsPattern && d.Gate.SkippedIn(platform) is null))
            {
                var path = Path.Combine(manifest.FolderPath, "bin", platform, assembly.Name);

                if (!File.Exists(path))
                    continue;

                var startup = StartupPath.Inspect(
                    path, assembly.SubModuleClass!, TypeAncestry.Folders(request.Environment.Entries, request.GameInstallPath, platform));

                PreflightFinding Row(PreflightCheck check, string headline, string detail, string subject) => new(
                    PreflightGrade.Critical,
                    check,
                    headline,
                    detail,
                    id,
                    environment => Disable(environment, id),
                    Strings.Current.Format("Core.Launcher.Preflight.BuiltForLine.FixLabel", entry.DisplayName),
                    Subject: subject);

                if (startup.ClassFault != StartupClassFault.None)
                {
                    findings.Add(Row(
                        PreflightCheck.SubModuleClassMissing,
                        Strings.Current.Format(
                            startup.ClassFault switch
                            {
                                StartupClassFault.ClassMissing => "Core.Launcher.Preflight.SubModuleClass.Headline.Missing",
                                StartupClassFault.NotASubModuleBase => "Core.Launcher.Preflight.SubModuleClass.Headline.NotBase",
                                StartupClassFault.ConstructorThrows => "Core.Launcher.Preflight.SubModuleClass.Headline.Throws",
                                _ => "Core.Launcher.Preflight.SubModuleClass.Headline.NoConstructor"
                            },
                            entry.DisplayName, assembly.SubModuleClass, assembly.Name),
                        Strings.Current[startup.ClassFault is StartupClassFault.NotASubModuleBase or StartupClassFault.ConstructorThrows or StartupClassFault.AbstractClass
                            ? "Core.Launcher.Preflight.SubModuleClass.Detail.Code"
                            : "Core.Launcher.Preflight.SubModuleClass.Detail"],
                        "class:" + assembly.SubModuleClass));
                    continue;
                }

                var gone = (missingApis?.Findings ?? [])
                    .Where(r => string.Equals(r.AssemblyPath, path, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (gone.Count == 0)
                    continue;

                var link = startup.Hierarchy
                    .Select(l => (Link: l, Reference: gone.FirstOrDefault(r => r.Kind == MissingApiKind.TypeMissing && r.MemberName is null && r.TypeName == l.DependsOn)))
                    .FirstOrDefault(x => x.Reference is not null);

                if (link.Reference is not null)
                {
                    findings.Add(Row(
                        PreflightCheck.SubModuleTypeLoad,
                        Strings.Current.Format(
                            "Core.Launcher.Preflight.SubModuleTypes.Headline", entry.DisplayName, ShortName(link.Link.Type), link.Reference.TypeName),
                        Strings.Current["Core.Launcher.Preflight.SubModuleTypes.Detail"],
                        "types:" + link.Link.Type) with { Faults = [LaunchEvidence.FaultOf(link.Reference)] });
                }

                // One row for a mod, naming the first use the game reaches: the fix is the same for all.
                if (missingApi)
                    continue;

                foreach (var use in startup.References)
                {
                    var reference = gone.FirstOrDefault(r => r.TypeName == use.TypeName
                        && (r.MemberName is null
                            ? r.Kind == MissingApiKind.TypeMissing
                            : r.MemberName == use.MemberName && r.Signature == use.Signature));

                    if (reference is null)
                        continue;

                    missingApi = true;

                    findings.Add(Row(
                        PreflightCheck.StartupMissingApi,
                        Strings.Current.Format(
                            "Core.Launcher.Preflight.Startup.Headline", entry.DisplayName, ShortMethod(use.Method), reference.Api),
                        Strings.Current.Format("Core.Launcher.Preflight.Startup.Detail", string.Join(" > ", use.Chain.Select(ShortMethod))),
                        "startup:" + use.Method + ">" + reference.Api) with { Faults = [LaunchEvidence.FaultOf(reference)] });
                    break;
                }

                // One row for a mod, naming the first use the game reaches when a campaign starts.
                foreach (var use in startup.CampaignReferences)
                {
                    var reference = gone.FirstOrDefault(r => r.TypeName == use.TypeName
                        && (r.MemberName is null
                            ? r.Kind == MissingApiKind.TypeMissing
                            : r.MemberName == use.MemberName && r.Signature == use.Signature));

                    if (reference is null)
                        continue;

                    findings.Add(Row(
                        PreflightCheck.CampaignStartMissingApi,
                        Strings.Current.Format(
                            "Core.Launcher.Preflight.Campaign.Headline", entry.DisplayName, ShortMethod(use.Method), reference.Api),
                        Strings.Current.Format("Core.Launcher.Preflight.Campaign.Detail", string.Join(" > ", use.Chain.Select(ShortMethod))),
                        "campaign:" + use.Method + ">" + reference.Api) with { Faults = [LaunchEvidence.FaultOf(reference)] });
                    break;
                }
            }
        }
    }

    // A type that leaves a game member unimplemented cannot be loaded. The game lists every type of a
    // SubModule's file as it loads it (Module.CollectModuleAssemblyTypes), so in such a file the first
    // failing type stops the game before the main menu: an Error. In any other file of the mod the same
    // type fails only where something lists or constructs it, which is a Warning. A file a module's
    // LoaderFilter names counts as a SubModule's file, since the loader lists its types at startup.
    private static void AddUnimplementedMembers(
        PreflightRequest request, MissingApiReport? missingApis, List<PreflightFinding> findings)
    {
        if (missingApis is null || missingApis.Unimplemented.Count == 0)
            return;

        var entries = request.Environment.Entries
            .GroupBy(e => e.Id)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var module in missingApis.Unimplemented.GroupBy(u => u.ModuleId))
        {
            if (!entries.TryGetValue(module.Key, out var entry) || entry.IsOfficial)
                continue;

            var all = module.ToList();

            var loadedFirst = all
                .Where(u => entry.Manifest?.DeclaredAssemblies?.Any(d =>
                    (d.SubModuleClass is not null && !d.IsPattern
                        && string.Equals(d.Name, Path.GetFileName(u.AssemblyPath), StringComparison.OrdinalIgnoreCase))
                    // The module loader lists every type of each file its LoaderFilter names while the
                    // game starts (Nemesis of Calradia on v1.5.7: GetTypes threw, then InitializeSubModuleBases).
                    || (d.IsPattern && d.Matches(Path.GetFileName(u.AssemblyPath)))) == true)
                .ToList();

            var certain = loadedFirst.Count > 0;
            var shown = certain ? loadedFirst : all;
            var first = shown[0];
            var others = shown.Count - 1;
            var id = module.Key;

            var detail = Strings.Current.Format(
                "Core.Launcher.Preflight.Unimplemented.Detail", first.TypeName, first.DeclaringType + "." + first.MemberName);

            if (others > 0)
                detail += " " + Strings.Current.Plural("Core.Launcher.Preflight.Unimplemented.Others", others);

            findings.Add(new PreflightFinding(
                certain ? PreflightGrade.Critical : PreflightGrade.Warning,
                PreflightCheck.UnimplementedMember,
                Strings.Current.Format(
                    "Core.Launcher.Preflight.Unimplemented.Headline", entry.DisplayName, ShortName(first.TypeName),
                    ShortName(first.DeclaringType) + "." + first.MemberName),
                detail,
                id,
                environment => Disable(environment, id),
                Strings.Current.Format("Core.Launcher.Preflight.BuiltForLine.FixLabel", entry.DisplayName),
                Subject: "unimplemented:" + first.TypeName + ">" + first.DeclaringType + "." + first.MemberName));
        }
    }

    // "SubModule.OnSubModuleLoad" out of "My.Mod.SubModule.OnSubModuleLoad".
    private static string ShortMethod(string method)
    {
        var member = method.LastIndexOf('.');

        // A constructor's own name starts with the dot.
        if (member > 0 && method[member - 1] == '.')
            member--;

        return member <= 0 ? method : ShortName(method[..member]) + method[member..];
    }

    // Gives every finding its identity on this mod's files and this game version, then lets the launches
    // on record raise it or refute it.
    private static void ApplyEvidence(PreflightRequest request, List<PreflightFinding> findings)
    {
        var platform = request.LoadedPlatformFolder ?? BannerlordEnvironmentManager.Core.Game.GameInstallLocator.StandardBinaryFolder;
        var version = request.GameVersion.IsEmpty ? string.Empty : request.GameVersion.ToString();
        var stamps = new Dictionary<ModuleId, string>();

        var folders = request.Environment.Entries
            .GroupBy(e => e.Id)
            .ToDictionary(g => g.Key, g => g.First().Manifest?.FolderPath);

        for (var i = 0; i < findings.Count; i++)
        {
            var finding = findings[i];

            if (finding.ModuleId is { } module && (finding.Grade == PreflightGrade.Critical || finding.Faults.Count > 0))
            {
                if (!stamps.TryGetValue(module, out var stamp))
                    stamps[module] = stamp = LaunchEvidence.StampOf(folders.GetValueOrDefault(module), platform);

                finding = finding with { EvidenceKey = LaunchEvidence.KeyFor(finding, stamp, version) };
            }

            findings[i] = LaunchEvidence.Judge(finding, request.History);
        }
    }

    // The method itself, the state machine the compiler writes for one that yields its targets, or a
    // lambda inside it.
    private static bool RunsWhilePatching(string method, string patchClass)
    {
        if (!method.StartsWith(patchClass, StringComparison.Ordinal))
            return false;

        var rest = method[patchClass.Length..];

        return rest is ".TargetMethod" or ".TargetMethods" or ".Prepare"
            || (rest.StartsWith('+') && (rest.Contains("<TargetMethod", StringComparison.Ordinal) || rest.Contains("<Prepare>", StringComparison.Ordinal)));
    }

    private static string ShortName(string typeName) => typeName[(typeName.LastIndexOfAny(['.', '+']) + 1)..];

    // Several mods on one method is Harmony working as designed, so it is raised only when a recent run's
    // own log names the method, which makes it a Warning. Every other overlap is computed and kept behind
    // Show Hidden. A pair with a transpiler used to be a Warning that said the game may die with an access
    // violation: six of those and seventy-nine notes stood on the owner's play install, which he plays
    // without crashing, so the shape alone proves nothing (2026-11-11).
    private static void AddPatchOverlaps(
        PreflightRequest request, List<PreflightFinding> findings, List<PreflightNote> notChecked)
    {
        if (request.Assemblies is not { Failed: false } assemblies)
            return;

        var report = HarmonyPatchOverlaps.Inspect(
            assemblies,
            request.Environment.Entries,
            request.LoadedPlatformFolder ?? MissingMembers.DefaultPlatformFolder,
            request.GameVersion);

        foreach (var problem in report.Problems)
            notChecked.Add(new PreflightNote(PreflightCheck.PatchOverlap, $"{problem.Path}: {problem.Reason}"));

        foreach (var overlap in report.Overlaps)
        {
            var named = !string.IsNullOrEmpty(request.RecentRunErrors)
                && request.RecentRunErrors.Contains(overlap.TargetMember, StringComparison.Ordinal);

            var grade = named ? PreflightGrade.Warning : PreflightGrade.Advisory;

            findings.Add(new PreflightFinding(
                grade,
                PreflightCheck.PatchOverlap,
                Strings.Current.Format(
                    "Core.Launcher.Preflight.PatchOverlap.Headline",
                    overlap.ShortTarget, overlap.Modules.Count, string.Join(", ", overlap.ModuleNames)),
                Strings.Current[overlap.HasTranspiler
                    ? "Core.Launcher.Preflight.PatchOverlap.Detail.Transpiler"
                    : "Core.Launcher.Preflight.PatchOverlap.Detail"],
                overlap.Modules[0],
                Relevance: named ? FindingRelevance.Relevant : FindingRelevance.Hidden,
                Subject: overlap.Target));
        }
    }

    private static bool IsMissingType(HarmonyPatchTarget target) => target.Verdict
        is HarmonyTargetVerdict.TypeMissing
        or HarmonyTargetVerdict.AssemblyNotInstalled
        or HarmonyTargetVerdict.AssemblyNotEnabled;

    // A patch class in a Harmony category runs only when the mod asks for that category by name, so its
    // missing target cannot stop the boot. CharacterReload's three BannerEditor patches were Critical on
    // the owner's install while the module loaded in every run (K20). Hidden, unless a recent run's log
    // shows that very patch class throwing, which lifts it to a Warning.
    //
    // Critical is the proven case only: the type is missing, nothing guards the patch, and the assembly
    // applies its patches from its submodule class with no catch, so the exception Harmony throws reaches
    // the game's own loader, which catches nothing either. Everywhere else the mod may catch it and lose
    // only the patch, which is a Warning.
    public static (PreflightGrade Grade, FindingRelevance Relevance) GradeHarmonyTarget(
        HarmonyPatchTarget target, string? recentRunErrors, PatchApplicationSite site = PatchApplicationSite.Unknown)
    {
        ArgumentNullException.ThrowIfNull(target);

        var missingType = IsMissingType(target);

        // A Prepare guard is the same shape as a category: Harmony asks the patch class first and skips it
        // when Prepare says no, which is how a mod writes code for a mod that may not be installed.
        // Nemesis of Calradia's RbmStartupPatch stands down without Realistic Battle Mod and was raised
        // as "patches RBM.SubModule.ApplyHarmonyPatches, which is not on this install".
        if ((target.Category is null && !target.GuardedByPrepare) || !missingType)
        {
            // A missing member kills the load the same way a missing type does when nothing catches the
            // patching: Bandit Warbands patched RaidEventComponent.OnBeforeFinalize, gone from v1.5.7, from
            // its own OnSubModuleLoad, and the game died at boot twice on his machine.
            // A patch in a category runs only when the mod asks for that category (TAOM's Patch14_Execution),
            // so even an uncaught site does not prove it is applied.
            var certain = site == PatchApplicationSite.UncaughtInSubModule && !target.GuardedByPrepare
                && target.Category is null
                && (missingType || target.Verdict == HarmonyTargetVerdict.MemberMissing);

            return (certain ? PreflightGrade.Critical : PreflightGrade.Warning, FindingRelevance.Relevant);
        }

        return ThrewInRecentRun(recentRunErrors, target.PatchClass)
            ? (PreflightGrade.Warning, FindingRelevance.Relevant)
            : (PreflightGrade.Advisory, FindingRelevance.Hidden);
    }

    // The short class name is what a stack frame or a Harmony message carries; the namespace often is not.
    private static bool ThrewInRecentRun(string? recentRunErrors, string patchClass)
    {
        if (string.IsNullOrEmpty(recentRunErrors) || string.IsNullOrWhiteSpace(patchClass))
            return false;

        var shortName = patchClass[(patchClass.LastIndexOfAny(['.', '+', '/']) + 1)..];

        return shortName.Length > 0 && recentRunErrors.Contains(shortName, StringComparison.Ordinal);
    }

    // One row per module rather than per reference: a mod built for another game version can carry
    // dozens, and a row each would bury every other finding in front of the Launch button.
    //
    // Graded by what the reference can do, not by how many there are. A missing type is the shape that
    // was observed killing a campaign start (MapTrackerManager, gone from the 1.4 line), so a module with
    // one is a Warning. A member or overload that is not there throws only when the method holding it
    // runs, which may be a feature the owner never touches, so on its own it stays Advisory.
    //
    // A recent run's own log naming the call (RecentRunErrors) lifts the row to a Warning that says the call
    // already threw. Still not Critical: Critical means the loader refuses, and this fails only when the
    // feature holding the call runs.
    private static MissingApiReport? AddMissingGameApis(
        PreflightRequest request, List<PreflightFinding> findings, List<PreflightNote> notChecked)
    {
        if (request.Assemblies is null)
        {
            notChecked.Add(new PreflightNote(
                PreflightCheck.MissingGameApi,
                Strings.Current["Core.Launcher.Preflight.MissingApi.NotIndexed"]));
            return null;
        }

        if (request.Assemblies.Failed)
        {
            notChecked.Add(new PreflightNote(
                PreflightCheck.MissingGameApi,
                Strings.Current.Format("Core.Launcher.Preflight.MissingApi.CouldNotRead", request.Assemblies.Error)));
            return null;
        }

        var report = MissingGameApis.Inspect(
            request.Assemblies, request.Environment.Entries, request.LoadedPlatformFolder, request.GameVersion);

        foreach (var problem in report.Problems)
            notChecked.Add(new PreflightNote(PreflightCheck.MissingGameApi, $"{problem.Path}: {problem.Reason}"));

        var named = string.IsNullOrEmpty(request.RecentRunErrors) ? [] : report.NamedBy(request.RecentRunErrors);

        var builtForLine = BuiltForLine.Inspect(
            report, request.Assemblies, request.Environment.Entries, request.LoadedPlatformFolder,
            request.GameVersion, request.OtherGameBuilds);

        foreach (var line in builtForLine)
        {
            var threw = named.Any(r => r.ModuleId == line.ModuleId);

            findings.Add(FromBuiltForLine(line, threw, request) with
            {
                CrashEvidenceUtc = threw ? LatestCrashWith(request.History, line.ModuleId) : null,
                Faults = FaultsOf(report.Findings.Where(r => r.ModuleId == line.ModuleId))
            });
        }

        var covered = builtForLine.Select(l => l.ModuleId).ToHashSet();

        foreach (var module in report.ByModule.Where(m => !covered.Contains(m.Key)))
        {
            var references = module.ToList();
            var first = references.FirstOrDefault(r => r.Kind == MissingApiKind.TypeMissing) ?? references[0];

            // A call the recent logs name is one the game really reached and failed on, so the row may
            // say it threw: the log, not the metadata, is the evidence.
            var thrown = named.FirstOrDefault(r => r.ModuleId == module.Key);

            if (thrown is not null)
            {
                findings.Add(new PreflightFinding(
                    PreflightGrade.Warning,
                    PreflightCheck.MissingGameApi,
                    Strings.Current.Format("Core.Launcher.Preflight.MissingApi.Thrown.Headline", thrown.ModuleName, thrown.Api),
                    Strings.Current["Core.Launcher.Preflight.MissingApi.Thrown.Detail"],
                    module.Key,
                    Subject: FindingKeys.WholeModule)
                {
                    AlternatePattern = FindingKeys.LegacyMissingApi(thrown.ModuleName),
                    Faults = FaultsOf(references)
                });
                continue;
            }

            // Hidden by default: a reference the metadata proves unresolvable is not a reference the game
            // runs, and the owner's install played through dozens of them. The module whose loaded build
            // binds only on another game line is the one MissingGameApi grade shown by default (the
            // built-for-line finding), and a call a recent log shows throwing is the other (above).
            findings.Add(new PreflightFinding(
                references.Any(r => r.Kind == MissingApiKind.TypeMissing || UsedByHarmonyPatch(r)) ? PreflightGrade.Warning : PreflightGrade.Advisory,
                PreflightCheck.MissingGameApi,
                Strings.Current.Plural("Core.Launcher.Preflight.MissingApi.Headline", references.Count, first.ModuleName, first.Api),
                Strings.Current["Core.Launcher.Preflight.MissingApi.Detail"],
                module.Key,
                Relevance: FindingRelevance.Hidden,
                Subject: FindingKeys.WholeModule)
            {
                AlternatePattern = FindingKeys.LegacyMissingApi(first.ModuleName),
                Faults = FaultsOf(references)
            });
        }

        return report;
    }

    private static IReadOnlyList<string> FaultsOf(IEnumerable<MissingApiReference> references) =>
        [.. references.Select(LaunchEvidence.FaultOf).Distinct(StringComparer.Ordinal)];

    // Critical: the module's loaded build was made for another game line, the shape behind DeathForAll's
    // auto-resolve crashes and MainQuestNoble's five native crashes. The fix turns the module off, which
    // the owner chooses by pressing it; the better remedy, installing the module's build for this line,
    // is named in the detail because only he can fetch it.
    private static PreflightFinding FromBuiltForLine(BuiltForLineFinding line, bool threw, PreflightRequest request)
    {
        var detail = Strings.Current.Format(
            line.Evidence == BuiltForLineEvidence.BindsOnOtherLine
                ? "Core.Launcher.Preflight.BuiltForLine.Detail.Binds"
                : "Core.Launcher.Preflight.BuiltForLine.Detail.Declared",
            line.ReferenceCount, line.FirstApi, line.BuiltForLine, line.GameLine);

        if (threw)
            detail += Strings.Current["Core.Launcher.Preflight.BuiltForLine.ThrownSuffix"];

        if (request.OtherArchives?.GetValueOrDefault(line.ModuleId.Value) is { Count: > 0 } others)
            detail += Strings.Current.Format("Core.Launcher.Preflight.BuiltForLine.OtherArchives", string.Join(", ", others));

        var id = line.ModuleId;

        return new PreflightFinding(
            PreflightGrade.Warning,
            PreflightCheck.BuiltForLine,
            Strings.Current.Format(
                "Core.Launcher.Preflight.BuiltForLine.Headline", line.ModuleName, line.ModuleVersion, line.BuiltForLine, line.GameLine),
            detail,
            id,
            environment => Disable(environment, id),
            Strings.Current.Format("Core.Launcher.Preflight.BuiltForLine.FixLabel", line.ModuleName),
            Subject: "line:" + line.BuiltForLine) { AlternatePattern = FindingKeys.LegacyMissingApi(line.ModuleName) };
    }

    // The exception names no mod; the frames under it do. Graded Warning like a call the logs show failing:
    // a past run really threw it, but it fires only when the code holding the loop runs, so it is not Critical.
    private static void AddCollectionModified(PreflightRequest request, List<PreflightFinding> findings)
    {
        if (string.IsNullOrEmpty(request.RecentRunErrors) || request.Assemblies is not { Failed: false } index)
            return;

        var enabled = request.Environment.Entries.Where(e => e.IsEnabled).ToDictionary(e => e.Id);

        foreach (var hit in CollectionModifiedErrors.Find(request.RecentRunErrors, index.Assemblies))
        {
            if (!enabled.TryGetValue(hit.ModuleId, out var entry))
                continue;

            findings.Add(new PreflightFinding(
                PreflightGrade.Warning,
                PreflightCheck.CollectionModified,
                Strings.Current.Format("Core.Launcher.Preflight.CollectionModified.Headline", entry.DisplayName),
                Strings.Current.Format("Core.Launcher.Preflight.CollectionModified.Detail", hit.Frame),
                hit.ModuleId,
                Subject: hit.Frame));
        }
    }

    // Critical: corrupted shared data does not heal, and the run of 2026-11-09 died 190 ms after the last
    // throw. The one safe repair is turning the mod off; its own fix has to come from its author.
    private static void AddParallelTickCorruption(PreflightRequest request, List<PreflightFinding> findings)
    {
        if (request.RecentFirstChance is not { Count: > 0 } traces)
            return;

        var enabled = request.Environment.Entries.Where(e => e.IsEnabled && !e.IsOfficial).ToDictionary(e => e.Id);

        foreach (var hit in BannerlordEnvironmentManager.Core.DryRun.ParallelTickCorruption.Find(traces))
        {
            if (!enabled.TryGetValue(hit.ModuleId, out var entry))
                continue;

            var id = entry.Id;

            findings.Add(new PreflightFinding(
                PreflightGrade.Warning,
                PreflightCheck.ParallelTickCorruption,
                Strings.Current.Format("Core.Launcher.Preflight.ParallelTickCorruption.Headline", entry.DisplayName),
                Strings.Current.Format("Core.Launcher.Preflight.ParallelTickCorruption.Detail", hit.Count, hit.ExceptionType, hit.Frame),
                id,
                environment => Disable(environment, id),
                Strings.Current.Format("Core.Launcher.Preflight.ParallelTickCorruption.FixLabel", entry.DisplayName),
                Subject: "parallel-tick"));
        }
    }

    // How long after an engine invoke failure a crash counts as having followed it. MainQuestNoble's
    // MapTrackerManager block came 37 to 100 seconds before each of its five native crashes.
    private static readonly TimeSpan CrashFollowsWithin = TimeSpan.FromSeconds(120);

    // Every engine invoke failure whose stack runs through an enabled community module, once per module,
    // target and kind of error. A module already named by a stronger finding (built for another line, a
    // call a recent log names, a collection modified mid-loop) is not named twice. Critical when the game
    // crashed within two minutes of the block, the proven MainQuestNoble shape; a Warning otherwise.
    private static void AddEngineInvokeFailures(PreflightRequest request, List<PreflightFinding> findings)
    {
        if (string.IsNullOrEmpty(request.RecentRunErrors))
            return;

        var byFolder = EnabledByFolder(request.Environment.Entries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var crash in EngineInvokeCrashes.Parse(request.RecentRunErrors))
        {
            if (crash.ModuleFolders.Select(folder => byFolder.GetValueOrDefault(folder)).FirstOrDefault(e => e is not null) is not { } entry)
                continue;

            var covered = findings.Any(f => f.ModuleId == entry.Id && !f.IsHiddenByDefault
                && f.Check is PreflightCheck.BuiltForLine or PreflightCheck.MissingGameApi or PreflightCheck.CollectionModified);

            if (covered || !seen.Add($"{entry.Id.Value}|{crash.TargetType}|{crash.MessageKind}"))
                continue;

            var crashEnd = CrashEndAfter(request.History, crash.When);
            int? crashedAfter = crashEnd is { } end && crash.When is { } when ? (int)Math.Round((end - when).TotalSeconds) : null;

            if (crash.IsCollectionModified)
            {
                findings.Add(new PreflightFinding(
                    PreflightGrade.Warning,
                    PreflightCheck.CollectionModified,
                    Strings.Current.Format("Core.Launcher.Preflight.CollectionModified.Headline", entry.DisplayName),
                    Strings.Current.Format("Core.Launcher.Preflight.CollectionModified.Detail", $"{crash.TargetType} ({crash.Invoked})"),
                    entry.Id,
                    Subject: $"{crash.TargetType}|{crash.Invoked}"));
                continue;
            }

            var detail = Strings.Current.Format(
                "Core.Launcher.Preflight.EngineInvoke.Detail", crash.TargetType, crash.Invoked, crash.InnerMessage, entry.DisplayName);

            if (crashedAfter is { } seconds)
                detail += Strings.Current.Format("Core.Launcher.Preflight.EngineInvoke.CrashedSuffix", seconds);

            findings.Add(new PreflightFinding(
                PreflightGrade.Warning,
                PreflightCheck.EngineInvokeFailure,
                Strings.Current.Format("Core.Launcher.Preflight.EngineInvoke.Headline", entry.DisplayName, crash.Invoked),
                detail,
                entry.Id,
                Subject: $"{crash.TargetType}|{crash.MessageKind}") { CrashEvidenceUtc = crashEnd });
        }
    }

    // The end of a launch that crashed within CrashFollowsWithin after the block.
    private static DateTimeOffset? CrashEndAfter(IReadOnlyList<LaunchRecord>? history, DateTimeOffset? when)
    {
        if (history is null || when is not { } at)
            return null;

        foreach (var record in history)
        {
            if (record.Outcome != LaunchOutcome.Crashed || record.EndedUtc is not { } ended)
                continue;

            var gap = new DateTimeOffset(DateTime.SpecifyKind(ended, DateTimeKind.Utc)) - at;

            if (gap >= TimeSpan.Zero && gap <= CrashFollowsWithin)
                return at + gap;
        }

        return null;
    }

    // Enabled community modules by the folder name a log path carries. The BUTR stack is left out: its
    // frames sit in nearly every stack (Harmony's MonoMod, ButterLib's crash reporter) without being the
    // module the failure ran through.
    private static Dictionary<string, ModuleEntry> EnabledByFolder(IReadOnlyList<ModuleEntry> entries)
    {
        var map = new Dictionary<string, ModuleEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (!entry.IsEnabled || entry.IsOfficial || entry.Manifest is not { } manifest || Prerequisites.ById(entry.Id.Value) is not null)
                continue;

            var folder = Path.GetFileName(manifest.FolderPath.TrimEnd('\\', '/'));

            if (folder.Length > 0)
                map.TryAdd(folder, entry);

            map.TryAdd(entry.Id.Value, entry);
        }

        return map;
    }

    // Warning: the engine logged objects it never registered, and an enabled module's data is why: a file of
    // the same kind failed validation, or a stylesheet removes the object. Names the module, never a guess
    // among every XML mod.
    private static void AddEngineDataLoadFailures(PreflightRequest request, List<PreflightFinding> findings)
    {
        if (string.IsNullOrEmpty(request.RecentRunErrors)
            || !request.RecentRunErrors.Contains(EngineDataLoadLog.NullMarker, StringComparison.Ordinal))
            return;

        var byFolder = EnabledByFolder(request.Environment.Entries);
        var dataByFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var official = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in request.Environment.Entries)
        {
            if (!entry.IsEnabled || entry.Manifest is not { } manifest)
                continue;

            var folder = Path.GetFileName(manifest.FolderPath.TrimEnd('\\', '/'));

            if (folder.Length == 0)
                continue;

            dataByFolder.TryAdd(folder, Path.Combine(manifest.FolderPath, "ModuleData"));

            if (entry.IsOfficial)
                official.Add(folder);
        }

        foreach (var cause in EngineDataLoadFailures.Find(request.RecentRunErrors, dataByFolder, official))
        {
            if (!byFolder.TryGetValue(cause.ModuleFolder, out var entry))
                continue;

            var shown = string.Join(", ", cause.MissingIds.Take(4)) + (cause.MissingIds.Count > 4 ? ", ..." : string.Empty);
            var headline = cause.RemovesByStylesheet
                ? Strings.Current.Format("Core.Launcher.Preflight.EngineDataLoad.RemovedHeadline", entry.DisplayName)
                : Strings.Current.Format("Core.Launcher.Preflight.EngineDataLoad.Headline", entry.DisplayName);
            var detail = cause.RemovesByStylesheet
                ? Strings.Current.Format("Core.Launcher.Preflight.EngineDataLoad.RemovedDetail", cause.File, cause.MissingIds.Count, shown)
                : Strings.Current.Format(
                    "Core.Launcher.Preflight.EngineDataLoad.Detail", cause.File, cause.Errors, cause.FirstMessage, cause.MissingIds.Count, shown);

            findings.Add(new PreflightFinding(
                PreflightGrade.Warning,
                PreflightCheck.EngineDataLoad,
                headline,
                detail,
                entry.Id,
                Subject: $"{cause.File}|data-load"));
        }
    }

    // Warning: one stale cache was followed by a native crash seconds later and once by nothing (K06).
    private static void AddStaleShaderCaches(PreflightRequest request, List<PreflightFinding> findings)
    {
        if (string.IsNullOrEmpty(request.RecentRunErrors))
            return;

        var byFolder = EnabledByFolder(request.Environment.Entries);

        foreach (var named in RunLogSignals.StaleShaderCacheModules(request.RecentRunErrors))
        {
            if (!byFolder.TryGetValue(named, out var entry))
                continue;

            // An explicit button, never part of Fix and launch: it moves the module's own shaders folders
            // into the cleared-file store as one batch, which Undo on that batch puts back.
            var store = request.ClearedFiles;
            var folder = entry.Manifest?.FolderPath;
            var clearable = store is not null && folder is not null && ShaderCacheClear.HasCache(folder);

            findings.Add(new PreflightFinding(
                PreflightGrade.Advisory,
                PreflightCheck.StaleShaderCache,
                Strings.Current.Format("Core.Launcher.Preflight.StaleShaderCache.Headline", entry.DisplayName),
                Strings.Current.Format("Core.Launcher.Preflight.StaleShaderCache.Detail", named),
                entry.Id,
                FixLabel: Strings.Current.Format("Core.Launcher.Preflight.StaleShaderCache.FixLabel", entry.DisplayName),
                IsDestructive: true,
                Remedy: clearable
                    ? () => ShaderCacheClear.Clear(
                        store!,
                        folder!,
                        Strings.Current.Format("Core.Launcher.Preflight.StaleShaderCache.ClearReason", entry.DisplayName),
                        DateTimeOffset.Now)
                    : null,
                Subject: "shader-cache"));
        }
    }

    // Warning where Windows refused a file (the mod it belongs to then fails to load, which looks like a crash
    // and is not one), Advisory where Windows only audited the refusal or Smart App Control is on and has
    // refused nothing yet. No button: the one safe step is a signed build, and BEM never advises turning
    // Smart App Control off, which Windows does not let anyone turn back on.
    private static void AddSmartAppControl(PreflightRequest request, List<PreflightFinding> findings)
    {
        if (request.CodeIntegrity is not { } integrity)
            return;

        var state = Strings.Current[$"Core.Launcher.Preflight.SmartAppControl.State.{integrity.State}"];
        var enabled = request.Environment.Entries.Where(e => e.IsEnabled && e.Manifest is not null).ToList();
        var named = 0;

        foreach (var block in integrity.Blocks)
        {
            var owner = enabled.FirstOrDefault(e =>
            {
                var folder = Path.GetFileName(e.Manifest!.FolderPath.TrimEnd('\\', '/'));

                return folder.Length > 0
                    && block.FilePath.Contains($"\\Modules\\{folder}\\", StringComparison.OrdinalIgnoreCase);
            });

            // A refusal of some other program's file is not about this launch. Only the process's own file name
            // counts: a test host under a "Bannerlord Environment Manager" folder loading its fixtures from Temp
            // was read as the game.
            var process = Path.GetFileName(block.Process.Replace('/', '\\'));

            if (owner is null && !process.StartsWith("Bannerlord.", StringComparison.OrdinalIgnoreCase)
                && !process.StartsWith("TaleWorlds", StringComparison.OrdinalIgnoreCase))
                continue;

            named++;

            var kind = block.Enforced ? "Blocked" : "Audited";
            var what = owner?.DisplayName ?? Strings.Current["Core.Launcher.Preflight.SmartAppControl.TheGame"];

            findings.Add(new PreflightFinding(
                block.Enforced ? PreflightGrade.Warning : PreflightGrade.Advisory,
                PreflightCheck.SmartAppControl,
                Strings.Current.Format($"Core.Launcher.Preflight.SmartAppControl.{kind}.Headline", what, block.FileName),
                Strings.Current.Format($"Core.Launcher.Preflight.SmartAppControl.{kind}.Detail", block.FilePath, state, block.EventId),
                owner?.Id,
                Subject: block.FileName));
        }

        if (named == 0 && integrity.State is SmartAppControlState.On or SmartAppControlState.Evaluation)
        {
            findings.Add(new PreflightFinding(
                PreflightGrade.Advisory,
                PreflightCheck.SmartAppControl,
                Strings.Current[$"Core.Launcher.Preflight.SmartAppControl.{integrity.State}.Headline"],
                Strings.Current[$"Core.Launcher.Preflight.SmartAppControl.{integrity.State}.Detail"],
                Subject: "smart-app-control"));
        }
    }

    // Warning: the log is the proof that Harmony did not load on that run, and nothing in the mod list causes it.
    private static void AddXboxHarmonyDependency(PreflightRequest request, List<PreflightFinding> findings)
    {
        if (string.IsNullOrEmpty(request.RecentRunErrors))
            return;

        var files = RunLogSignals.XboxHarmonyUnresolvedFiles(request.RecentRunErrors);

        if (files.Count == 0)
            return;

        var list = string.Join(", ", files);

        findings.Add(new PreflightFinding(
            PreflightGrade.Warning,
            PreflightCheck.XboxHarmonyDependency,
            Strings.Current.Format("Core.Launcher.Preflight.XboxHarmony.Headline", list),
            Strings.Current.Format("Core.Launcher.Preflight.XboxHarmony.Detail", list, RunLogSignals.XboxHarmonyIssueUrl),
            Subject: "xbox-harmony-dependencies"));
    }

    // Warning: the Exception Manager is on by default, and without Harmony a crash on this launch opens no
    // window and leaves no record (proven on v1.0.1, where the instance had no Harmony). Harmony installed
    // but off is turned on by the fix; Harmony absent is installed from Library with the BUTR stack.
    private static void AddExceptionManagerUnarmed(PreflightRequest request, List<PreflightFinding> findings)
    {
        if (!request.ExceptionManagerOn)
            return;

        static bool IsHarmony(ModuleEntry entry) =>
            string.Equals(entry.Id.Value, BannerlordEnvironmentManager.Core.DryRun.CompanionManifest.HarmonyModuleId, StringComparison.OrdinalIgnoreCase);

        var harmony = request.Environment.Entries.FirstOrDefault(IsHarmony);

        if (harmony is { IsEnabled: true })
            return;

        var installed = harmony is not null;

        findings.Add(new PreflightFinding(
            PreflightGrade.Warning,
            PreflightCheck.ExceptionManagerUnarmed,
            Strings.Current["Core.Launcher.Preflight.ExceptionManagerUnarmed.Headline"],
            Strings.Current[installed
                ? "Core.Launcher.Preflight.ExceptionManagerUnarmed.DetailOff"
                : "Core.Launcher.Preflight.ExceptionManagerUnarmed.DetailMissing"],
            installed ? harmony!.Id : null,
            Fix: installed ? env => env.WithEnabled(true, IsHarmony) : null,
            FixLabel: installed ? Strings.Current["Core.Launcher.Preflight.ExceptionManagerUnarmed.FixLabel"] : "Fix",
            Subject: "exception-manager-unarmed"));
    }

    // Critical when the invalid file is a SubModule's DLLName: TaleWorlds.MountAndBlade Module.cs
    // LoadSubModules loads it with AssemblyLoader.LoadFrom, which answers CriticalError, and
    // HandleSubmoduleLoadError collects that, after which LoadSubModules shows the error box and throws, so
    // the game never reaches the menu. Warning for a plain <Assemblies> entry: LoadFrom shows "Cannot load:"
    // and LoadSubModules records LoadedWithErrors and goes on. (K11)
    private static void AddInvalidDeclaredAssemblies(PreflightRequest request, List<PreflightFinding> findings)
    {
        foreach (var invalid in InvalidDeclaredAssemblies.Find(request.Environment.Entries, request.LoadedPlatformFolder))
        {
            var archive = request.RecordedArchives?.GetValueOrDefault(invalid.ModuleId.Value);

            findings.Add(new PreflightFinding(
                invalid.IsSubModuleDll ? PreflightGrade.Critical : PreflightGrade.Warning,
                PreflightCheck.InvalidDeclaredAssembly,
                Strings.Current.Format(
                    "Core.Launcher.Preflight.InvalidAssembly.Headline", invalid.DisplayName, Path.GetFileName(invalid.FilePath)),
                Strings.Current.Format(
                    invalid.IsSubModuleDll
                        ? "Core.Launcher.Preflight.InvalidAssembly.Detail.SubModule"
                        : "Core.Launcher.Preflight.InvalidAssembly.Detail",
                    invalid.Reason),
                invalid.ModuleId,
                FixLabel: archive is null ? "Fix" : Strings.Current["Core.Launcher.Preflight.InvalidAssembly.FixLabel"],
                // Destructive only in the sense that keeps it out of Fix All: it opens a page, it mends nothing.
                IsDestructive: archive is not null,
                Remedy: archive is not null && request.ShowArchive is { } show ? () => show(archive) : null,
                Subject: Path.GetFileName(invalid.FilePath)));
        }
    }

    // One finding per group of modules that share exactly the same crashing launches and appear in no clean
    // one. Only modules enabled now are named, since the finding is about this launch.
    private static void AddCrashHistory(PreflightRequest request, List<PreflightFinding> findings)
    {
        if (request.History is not { Count: > 0 } history)
            return;

        var enabled = request.Environment.Entries.Where(e => e.IsEnabled && !e.IsOfficial).ToList();
        var byId = enabled.GroupBy(e => e.Id.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var suspects = LaunchHistoryDifferential.Find(history, byId.Keys);

        // Catalog gap G7: no managed frame and no differential means the cause is unknown, and the honest
        // next step is the experiment, never a guessed mod. Two in a row, so one bad run says nothing.
        var lastTwo = history.Where(r => r.Outcome != LaunchOutcome.Running)
            .OrderByDescending(r => r.StartedUtc).Take(2).ToList();

        if (suspects.Count == 0 && lastTwo.Count == 2 && lastTwo.All(r => r.Outcome == LaunchOutcome.Crashed))
        {
            findings.Add(new PreflightFinding(
                PreflightGrade.Warning,
                PreflightCheck.CrashCauseUnknown,
                Strings.Current["Core.Launcher.Preflight.CrashCauseUnknown.Headline"],
                Strings.Current["Core.Launcher.Preflight.CrashCauseUnknown.Detail"]));
        }

        foreach (var set in suspects)
        {
            var entries = set.ModuleIds.Select(id => byId[id]).ToList();
            var ids = entries.Select(e => e.Id).ToHashSet();
            var names = string.Join(", ", entries.Select(e => e.DisplayName));

            findings.Add(new PreflightFinding(
                PreflightGrade.Warning,
                PreflightCheck.CrashHistory,
                Strings.Current.Format("Core.Launcher.Preflight.CrashHistory.Headline", names, set.CrashingLaunchesWithThem),
                Strings.Current.Format("Core.Launcher.Preflight.CrashHistory.Detail", set.CrashingLaunches, set.CleanLaunches),
                entries[0].Id,
                environment => environment.WithEntries([.. environment.Entries.Select(entry =>
                    ids.Contains(entry.Id) && !entry.IsOfficial ? entry with { IsEnabled = false } : entry)]),
                Strings.Current.Plural("Core.Launcher.Preflight.CrashHistory.FixLabel", entries.Count, names),
                Subject: string.Join(",", set.ModuleIds)) { CrashEvidenceUtc = LatestCrashWith(history, entries[0].Id) });
        }
    }

    // Graded Warning, never Critical: the log shows the burst and how the run ended, nothing more. A run that
    // ended abnormally is worded as the burst having preceded that crash; any other run as a past burst.
    private static void AddRenderRequestBurst(PreflightRequest request, List<PreflightFinding> findings)
    {
        if (request.RecentRenderBurst is not { } burst)
            return;

        var key = burst.EndedAbnormally ? "Crash" : "Past";

        findings.Add(new PreflightFinding(
            PreflightGrade.Advisory,
            PreflightCheck.RenderRequestBurst,
            Strings.Current.Format($"Core.Launcher.Preflight.RenderBurst.{key}.Headline", burst.Count, burst.MostRequestedItem),
            Strings.Current.Format($"Core.Launcher.Preflight.RenderBurst.{key}.Detail", burst.LogName, burst.MostRequestedItem),
            Subject: burst.MostRequestedItem));
    }

    // A Harmony patch runs on the game's own hot paths (a battle round, a survival roll), so a missing call
    // inside one fails for every player who reaches that path, unlike a feature a player may never open.
    // DeathForAll's survival-chance patch called a perk helper overload 1.4.8 lacks and crashed every auto-resolve.
    private static bool UsedByHarmonyPatch(MissingApiReference reference) =>
        reference.UsedBy.Any(method => method.Contains("Patch", StringComparison.OrdinalIgnoreCase));

    private static void AddShadowedAssemblies(
        PreflightRequest request, List<PreflightFinding> findings, List<PreflightNote> notChecked)
    {
        if (request.Assemblies is null)
        {
            notChecked.Add(new PreflightNote(
                PreflightCheck.ShadowedAssembly,
                Strings.Current["Core.Launcher.Preflight.ShadowedAssembly.NotIndexed"]));
            return;
        }

        if (request.Assemblies.Failed)
        {
            notChecked.Add(new PreflightNote(
                PreflightCheck.ShadowedAssembly,
                Strings.Current.Format(
                    "Core.Launcher.Preflight.ShadowedAssembly.CouldNotRead", request.Assemblies.Error)));
            return;
        }

        var found = DuplicateAssemblies.Find(
            request.Assemblies, request.Environment.Entries, request.LoadedPlatformFolder);

        // A winning copy the bundled-stack finding will set aside is already answered there, and this
        // finding's "BEM cannot fix this safely" would contradict the button that does fix it.
        var setAside = request.SetAsideStore is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : BundledStackCopies.Find(request.Environment.Entries)
                .SelectMany(copy => copy.Files.Select(file => Path.GetFullPath(Path.Combine(copy.BundlerFolder, file))))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var versions = found.Count == 0 ? null : GameVersionSupport.Inspect(request.Environment.Entries, request.GameVersion);

        foreach (var shadowed in found)
        {
            if (setAside.Contains(Path.GetFullPath(shadowed.Winner.Path)))
                continue;

            // A game version's build the set's loader passes over on this game is never opened.
            if (HealthJudgement.IsDormantBuild(shadowed.FileName, versions))
                continue;

            var newer = shadowed.Shadowed
                .Where(s => s.Difference == AssemblyCopyDifference.NewerThanTheOneThatBinds)
                .ToList();

            if (newer.Count == 0)
                continue;

            var apiGap = newer.Where(s =>
                s.Copy.Version.Major != shadowed.Winner.Version.Major
                || s.Copy.Version.Minor != shadowed.Winner.Version.Minor).ToList();

            var shippers = string.Join(", ", newer.Select(s => $"'{s.Copy.DisplayName}' ({s.Copy.Version})"));

            findings.Add(new PreflightFinding(
                apiGap.Count > 0 ? PreflightGrade.Warning : PreflightGrade.Advisory,
                PreflightCheck.ShadowedAssembly,
                Strings.Current.Format(
                    "Core.Launcher.Preflight.ShadowedAssembly.Headline", shippers, shadowed.FileName),
                Strings.Current.Plural(
                    "Core.Launcher.Preflight.ShadowedAssembly.Detail",
                    shadowed.DependentModules.Count,
                    shadowed.Winner.Version,
                    shadowed.Winner.DisplayName),
                newer[0].Copy.ModuleId,
                Subject: FindingKeys.ShadowedAssemblySubject(shadowed.FileName)) { AlternateKeys = [FindingKeys.LegacyShadowedAssembly(shadowed)] });
        }
    }

    // A patch gap inside the game's own line ran on the owner's install every time (ArenaOverhaul 1.4.7,
    // MarryAnyone 1.4.5, MBFastDialogue 1.4.6 on 1.4.8), so GameVersionSupport no longer reports one. What
    // is left crosses a line, which is what took Cultured Start Reloaded's 1.4 build down on game 1.5.2.
    //
    // A build made for another line does harm through what it calls that this game no longer has, and the
    // missing-API scan reads every such call from the build that loads. Where that scan read the module's
    // assemblies and found nothing missing, the gap is a fact with no consequence shown (Cultured Start
    // Reloaded's v1.3.4 build on game v1.4.8, played daily), so it is kept behind Show Hidden. It stays a
    // Warning where the scan did not run or could not read the module.
    private static void AddGameVersionGaps(
        PreflightRequest request, List<PreflightFinding> findings, List<PreflightNote> notChecked,
        MissingApiReport? missingApis = null)
    {
        if (request.GameVersion.IsEmpty)
        {
            notChecked.Add(new PreflightNote(
                PreflightCheck.GameVersion,
                Strings.Current["Core.Launcher.Preflight.GameVersion.NotChecked"]));
            return;
        }

        // Only gaps that cross a game line or leave the loader nothing to open are reported at all
        // (GameVersionSupport), and both shapes crashed real runs, so every one is a Warning.
        foreach (var gap in GameVersionSupport.Inspect(request.Environment.Entries, request.GameVersion).Gaps)
        {
            var readClean = HealthJudgement.OfVersionGap(gap.Set.ModuleId, missingApis) == HealthVerdict.Nothing;

            findings.Add(new PreflightFinding(
                readClean ? PreflightGrade.Advisory : PreflightGrade.Warning,
                PreflightCheck.GameVersion,
                gap.Headline,
                gap.Why,
                gap.Set.ModuleId,
                Relevance: readClean ? FindingRelevance.Hidden : FindingRelevance.Relevant,
                Subject: FindingKeys.VersionGapSubject(gap.Set)) { AlternateKeys = FindingKeys.LegacyVersionGap(gap) });
        }
    }

    // Critical without qualification. A dataset with nothing left in it is not a balance change or a
    // conflict between two mods, it is the game reading an empty list where it expects its own content,
    // and whatever breaks does so long after the module responsible has finished loading.
    private static void AddEmptiedDatasets(
        PreflightRequest request, List<PreflightFinding> findings, List<PreflightNote> notChecked)
    {
        // With nothing supplied, an install where no enabled module registers a stylesheet is still fully
        // answered: there is nothing that could empty a dataset. Only an install that has stylesheets and
        // no stored result is "not checked" (XmlTransformCache.Ensure computes one in the background).
        var transforms = request.Transforms
            ?? (XmlDatasetOverlaps.HasStylesheets(request.Environment.Entries) ? null : []);

        if (transforms is null)
        {
            notChecked.Add(new PreflightNote(
                PreflightCheck.DatasetEmptiedByStylesheet,
                Strings.Current["Core.Launcher.Preflight.EmptiedDataset.NotChecked"]));
            return;
        }

        foreach (var transform in transforms.Where(t => t.EmptiesDataset))
        {
            findings.Add(new PreflightFinding(
                PreflightGrade.Warning,
                PreflightCheck.DatasetEmptiedByStylesheet,
                Strings.Current.Format(
                    "Core.Launcher.Preflight.EmptiedDataset.Headline", transform.DisplayName, transform.Dataset),
                transform.Describe(),
                transform.ModuleId,
                Subject: transform.Dataset));
        }
    }

    // Only reported when something enabled actually declares it. A setup with no mod needing ButterLib
    // is not missing ButterLib, and saying so would put a permanent complaint in front of a launch that
    // works, which is how a preflight gets ignored.
    // One finding per redundant module rather than one per class, because three modules bundling six
    // classes between them is three things to switch off and six rows saying so is what buries them.
    // The module the game reaches first keeps the declaration; every module after it is offering a
    // second setup of a class that is already being set up.
    //
    // Graded Warning and not Critical. That the class is constructed twice is what the loader does and
    // not an opinion, but whether the second pass is survivable belongs to the class, so the grade says
    // worth stopping for rather than certain to fail. It still reaches the launch dialog, and Fix and
    // Launch switches every redundant module off in one press.
    // A module that registers a merged dataset with only a stylesheet is harmless anywhere but first
    // among the modules registering it. First, CreateMergedXmlFile opens slot zero's path unchecked and
    // throws on the empty one, so this is an Error and replaces the "loads before Native" convention
    // note for the same module, which would otherwise read as a matter of taste.
    private static void AddMergeFirstFileMissing(PreflightRequest request, List<PreflightFinding> findings)
    {
        var faults = XmlMergeFaults.FirstFileMissing(request.Environment.Entries);

        if (faults.Count == 0)
            return;

        foreach (var group in faults.GroupBy(f => f.Trigger.ModuleId))
        {
            findings.RemoveAll(f => f.Check == PreflightCheck.LoadOrder
                && f.ModuleId == group.Key
                && f.Subject is { } subject
                && subject.StartsWith($"{IssueKind.ContentBeforeNative}:", StringComparison.Ordinal));

            foreach (var fault in group)
            {
                var stylesheet = fault.Sources.Count > 0 ? fault.Sources[0].Path : fault.Trigger.Path;

                findings.Add(new PreflightFinding(
                    PreflightGrade.Critical,
                    PreflightCheck.MergeFirstFileMissing,
                    Strings.Current.Format("Core.Launcher.Preflight.MergeFirstFileMissing.Headline",
                        fault.Trigger.DisplayName, fault.Dataset),
                    Strings.Current.Format("Core.Launcher.Preflight.MergeFirstFileMissing.Detail",
                        fault.Trigger.DisplayName, fault.Dataset, stylesheet),
                    group.Key,
                    Subject: $"MergeFirstFileMissing:{fault.Dataset}"));
            }
        }
    }

    // Critical: TaleWorlds.ObjectSystem MBObjectManager.cs ApplyXslt loads the stylesheet with
    // XslCompiledTransform.Load and GetMergedXmlForManaged calls it before LoadXML's try, so a stylesheet
    // that does not parse or compile ends the load. The first slot of a dataset is never applied and is not
    // judged. Nothing here is repairable by BEM; the module's author has to fix the file.
    private static void AddInvalidStylesheets(PreflightRequest request, List<PreflightFinding> findings)
    {
        foreach (var fault in XmlDatasetOverlaps.StylesheetFaults(request.Environment.Entries))
        {
            var id = fault.ModuleId;

            findings.Add(new PreflightFinding(
                PreflightGrade.Critical,
                PreflightCheck.StylesheetInvalid,
                Strings.Current.Format(
                    "Core.Launcher.Preflight.Stylesheet.Headline", fault.DisplayName, Path.GetFileName(fault.Path), fault.Dataset),
                Strings.Current.Format("Core.Launcher.Preflight.Stylesheet.Detail",
                    fault.Modes.Count is > 0 and < 2 ? fault.Message + " (game mode " + fault.Modes[0] + ")" : fault.Message),
                id,
                Subject: $"stylesheet:{fault.Dataset}:{Path.GetFileName(fault.Path)}")
            {
                TurnOff = environment => Disable(environment, id),
                TurnOffLabel = Strings.Current.Format("Core.Launcher.Preflight.BuiltForLine.FixLabel", fault.DisplayName)
            });
        }
    }

    // Critical: applying the patch runs the type's static constructor while the module loads, GameTexts has no
    // text manager yet, and the runtime keeps the TypeInitializationException (see TextTableStaticInitializers).
    // One row for each mod and type, each with its own turn-off button and no fix: a reorder only helps when a
    // module that fills the text table early loads first, which cannot be read, and every patching mod has to
    // go for the crash to, so the rows are equal choices.
    private static void AddTextTableStaticInitializers(
        PreflightRequest request, List<PreflightFinding> findings, List<PreflightNote> notChecked)
    {
        if (request.Assemblies is not { Failed: false } assemblies)
            return;

        var report = TextTableStaticInitializers.Inspect(assemblies, request.Environment.Entries);

        if (!report.Looked)
        {
            notChecked.Add(new PreflightNote(
                PreflightCheck.TextTableStaticInitializer,
                Strings.Current["Core.Launcher.Preflight.TextTable.NotChecked"]));
            return;
        }

        foreach (var patch in report.Patches)
        {
            var id = patch.ModuleId;
            var entry = request.Environment.Entries.FirstOrDefault(e => e.Id == id);
            var turnOff = entry is { IsOfficial: false };

            findings.Add(new PreflightFinding(
                PreflightGrade.Critical,
                PreflightCheck.TextTableStaticInitializer,
                Strings.Current.Format("Core.Launcher.Preflight.TextTable.Headline", patch.ModuleName, patch.TypeFullName),
                Strings.Current.Format("Core.Launcher.Preflight.TextTable.Detail", ShortName(patch.TypeFullName)),
                id,
                Subject: "texttable:" + patch.TypeFullName)
            {
                TurnOff = turnOff ? environment => Disable(environment, id) : null,
                TurnOffLabel = Strings.Current.Format("Core.Launcher.Preflight.BuiltForLine.FixLabel", patch.ModuleName)
            });
        }
    }

    private static void AddDuplicateSubModuleClasses(PreflightRequest request, List<PreflightFinding> findings)
    {
        var clashes = DuplicateSubModuleClasses.Find(request.Environment.Entries);

        if (clashes.Count == 0)
            return;

        var covered = AddBundledStackCopies(request, findings);

        // A class the bundled-copy finding already answers would otherwise be answered twice, once by
        // setting the copy aside and once by disabling the installed module, and Fix and Launch would
        // apply both and leave neither.
        var byModule = clashes
            .Where(clash => !covered.Contains(clash.ClassName))
            .SelectMany(clash => clash.Later.Select(module => (Module: module, clash.ClassName)))
            .GroupBy(item => item.Module)
            .ToList();

        foreach (var group in byModule)
        {
            var moduleId = group.Key;
            var entry = request.Environment.Entries.FirstOrDefault(e => e.Id == moduleId);

            // Disabling an official module breaks the game rather than the load order, and nothing that
            // ships the BUTR stack is one, so a match here means the rule has found something it does
            // not understand and the finding goes out without a button.
            var fixable = entry is { IsOfficial: false };
            var names = string.Join(", ", group.Select(item => item.ClassName));
            var first = clashes.First(clash => clash.Later.Contains(moduleId)).First;
            var headline = Strings.Current.Plural(
                "Core.Launcher.Preflight.DuplicateSubModuleClass.Headline",
                group.Count(),
                entry?.DisplayName ?? moduleId.Value,
                first.Value);
            var detail = Strings.Current.Format(
                "Core.Launcher.Preflight.DuplicateSubModuleClass.Detail", names, first.Value);
            var disableLabel = Strings.Current.Format(
                "Core.Launcher.Preflight.DuplicateSubModuleClass.FixLabel",
                entry?.DisplayName ?? moduleId.Value);
            var alternateKeys = clashes.Where(clash => clash.Later.Contains(moduleId)).Select(FindingKeys.LegacySubModuleClash).ToList();

            // The keep-on fix: the later module stops declaring the classes the first module already
            // constructs, so each is built once and the mod stays on. Undoable from the set-aside store.
            if (fixable && request.SetAsideStore is { } store && DuplicateSubModuleClasses.DeclareOncePlan(request.Environment.Entries, moduleId) is not null)
            {
                var copy = DuplicateSubModuleClasses.DeclareOncePlan(request.Environment.Entries, moduleId)!;

                findings.Add(new PreflightFinding(
                    PreflightGrade.Warning,
                    PreflightCheck.DuplicateSubModuleClass,
                    headline,
                    detail,
                    moduleId,
                    FixLabel: Strings.Current.Format(
                        "Core.Launcher.Preflight.DuplicateSubModuleClass.DeclareOnceLabel", entry.DisplayName),
                    Remedy: () => store.Apply(copy),
                    Subject: names)
                {
                    AlternateKeys = alternateKeys,
                    TurnOff = environment => Disable(environment, moduleId),
                    TurnOffLabel = disableLabel
                });

                continue;
            }

            findings.Add(new PreflightFinding(
                PreflightGrade.Warning,
                PreflightCheck.DuplicateSubModuleClass,
                headline,
                detail,
                moduleId,
                fixable ? environment => Disable(environment, moduleId) : null,
                disableLabel,
                Subject: names)
            {
                AlternateKeys = alternateKeys
            });
        }
    }

    // Returns the classes it answered, so the plain duplicate finding leaves them alone. A run on Tales
    // from the Age of Men showed both halves: with its bundled copies beside the installed stack the
    // boot crashed in Bannerlord_MBOptionScreen, and with the copies set aside every module loaded.
    private static HashSet<string> AddBundledStackCopies(PreflightRequest request, List<PreflightFinding> findings)
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);

        if (request.SetAsideStore is not { } store)
            return covered;

        foreach (var copy in BundledStackCopies.Find(request.Environment.Entries))
        {
            var entry = request.Environment.Entries.First(e => e.Id == copy.Bundler);
            var name = entry.DisplayName;
            var stack = string.Join(", ", copy.StackModules.Select(id =>
                Prerequisites.ById(id.Value)?.Name ?? id.Value));

            covered.UnionWith(copy.SubModuleClasses);

            findings.Add(new PreflightFinding(
                PreflightGrade.Warning,
                PreflightCheck.BundledStackCopy,
                Strings.Current.Format("Core.Launcher.Preflight.BundledStack.Headline", name, stack),
                Strings.Current.Format(
                    "Core.Launcher.Preflight.BundledStack.Detail",
                    string.Join(", ", copy.SubModuleClasses)),
                copy.Bundler,
                FixLabel: Strings.Current["Core.Launcher.Preflight.BundledStack.FixLabel"],
                Remedy: () => store.Apply(copy)));
        }

        return covered;
    }

    private static ModuleEnvironment Disable(ModuleEnvironment environment, ModuleId id) =>
        environment.WithEntries([.. environment.Entries.Select(entry =>
            entry.Id == id && !entry.IsOfficial ? entry with { IsEnabled = false } : entry)]);

    private static void AddMissingPrerequisites(PreflightRequest request, List<PreflightFinding> findings)
    {
        var enabled = request.Environment.Entries.Where(entry => entry.IsEnabled).ToList();

        var declared = enabled
            .SelectMany(entry => entry.Dependencies)
            .Where(dependency => !dependency.IsOptional && !dependency.IsIncompatible)
            .Select(dependency => dependency.TargetId.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var state in Prerequisites.Inspect(request.GameInstallPath, enabled.Select(entry => entry.Id)))
        {
            if (state.IsInstalled)
                continue;

            // The loader is the exception: nothing declares it, because it is not a module. It is only
            // reported when BEM was given somewhere to look, so "not checked" never reads as "missing".
            var isLoader = state.Prerequisite.Kind == PrerequisiteKind.Loader;

            if (isLoader
                ? string.IsNullOrWhiteSpace(request.GameInstallPath)
                : !declared.Contains(state.Prerequisite.Id))
            {
                continue;
            }

            var whoNeedsIt = enabled
                .Where(entry => entry.Dependencies.Any(dependency =>
                    !dependency.IsOptional
                    && !dependency.IsIncompatible
                    && string.Equals(dependency.TargetId.Value, state.Prerequisite.Id, StringComparison.OrdinalIgnoreCase)))
                .Select(entry => entry.DisplayName)
                .Take(NamesShown)
                .ToList();

            findings.Add(new PreflightFinding(
                PreflightGrade.Warning,
                PreflightCheck.MissingPrerequisite,
                Strings.Current.Format("Core.Launcher.Preflight.Prereq.Headline", state.Prerequisite.Name),
                $"{state.Evidence} {state.Prerequisite.Needs} {state.Prerequisite.WithoutIt}"
                + (whoNeedsIt.Count > 0
                    ? Strings.Current.Format("Core.Launcher.Preflight.Prereq.DeclaredBy", string.Join(", ", whoNeedsIt))
                    : string.Empty)
                + Strings.Current.Format(
                    "Core.Launcher.Preflight.Prereq.PageAndSource",
                    state.Prerequisite.NexusPage,
                    state.Prerequisite.SourceRepository)));
        }
    }

    private static void AddRigConflicts(PreflightRequest request, List<PreflightFinding> findings)
    {
        var report = RigConflicts.Inspect(request.Environment.Entries);

        if (report.OrderFix is { IsNeeded: true } fix && fix.AnchorId is { } anchor)
        {
            findings.Add(new PreflightFinding(
                PreflightGrade.Warning,
                PreflightCheck.RigConflict,
                Strings.Current.Plural(
                    "Core.Launcher.Preflight.RigConflict.AnchorHeadline", fix.AboveAnchor.Count, anchor),
                Strings.Current.Format("Core.Launcher.Preflight.RigConflict.AnchorDetail", anchor),
                anchor,
                MoveTheRigAnchorFirst,
                Strings.Current.Format("Core.Launcher.Preflight.RigConflict.AnchorFixLabel", anchor)));
        }

        foreach (var conflict in report.Conflicts)
        {
            var names = string.Join(", ", conflict.Patchers.Select(p => $"'{p.DisplayName}'"));

            findings.Add(new PreflightFinding(
                PreflightGrade.Advisory,
                PreflightCheck.RigConflict,
                Strings.Current.Plural(
                    "Core.Launcher.Preflight.RigConflict.PlaceholderHeadline",
                    conflict.Patchers.Count,
                    conflict.ActionSetId),
                Strings.Current.Format(
                    "Core.Launcher.Preflight.RigConflict.PlaceholderDetail", names, conflict.AppliedLast.DisplayName),
                conflict.AppliedLast.ModuleId));
        }
    }

    // Recomputed against whatever environment the fix is handed rather than closing over the one that
    // was inspected: the user may have moved something between reading the finding and pressing the
    // button, and applying a stale list would undo that move.
    private static ModuleEnvironment MoveTheRigAnchorFirst(ModuleEnvironment environment)
    {
        var fix = RigConflicts.PlanNativeFirst(environment.Entries);

        return fix.IsNeeded ? environment.WithEntries(fix.Corrected) : environment;
    }

    // Optional, and absent rather than broken when it is off: no scan has run means the check is named
    // in NotChecked and nothing else changes. A scan that has run is worth reading before launching
    // what it flagged, which is the one moment the finding is actionable.
    private static void AddSafety(
        PreflightRequest request, List<PreflightFinding> findings, List<PreflightNote> notChecked)
    {
        if (request.Safety is null)
        {
            notChecked.Add(new PreflightNote(
                PreflightCheck.ModSafety,
                Strings.Current["Core.Launcher.Preflight.Safety.NotChecked"]));
            return;
        }

        var enabled = request.Environment.Entries
            .Where(e => e.IsEnabled && e.Manifest is not null)
            .ToDictionary(e => e.Manifest!.FolderPath, e => e, StringComparer.OrdinalIgnoreCase);

        foreach (var result in request.Safety.Where(r => r.IsAlarm))
        {
            if (!enabled.TryGetValue(result.Target.Path, out var entry))
                continue;

            findings.Add(new PreflightFinding(
                PreflightGrade.Advisory,
                PreflightCheck.ModSafety,
                Strings.Current.Format(
                    "Core.Launcher.Preflight.Safety.Headline", entry.DisplayName, result.VerdictText),
                Strings.Current.Format("Core.Launcher.Preflight.Safety.Detail", result.VerdictExplanation),
                entry.Id,
                AlternateKey: AcceptedFindingStore.KeyFor("ModSafety", result.FindingHeadline)));
        }
    }

    // How many of the longest ids are worth naming. Enough to cover the deficit of every overflow
    // seen so far without turning the row into the load order over again.
    private const int LongestIdsShown = 10;

    // The one check here that is arithmetic rather than inference. Bannerlord copies its whole
    // argument string into a 4096-byte buffer with strcpy_s, and a string that does not fit is not
    // truncated: the C runtime fast-fails and Windows ends the process with 0xC0000409 about two
    // seconds in, inside the native entry point, before the CLR has loaded one game assembly. There is
    // no crash report, no rgl log and no BUTR report, because nothing managed ever runs to write one.
    // See GameCommandLine for the disassembly.
    //
    // So this is graded Critical when it does not fit, and that grading is not a judgment: it is what
    // the loader does. It is also the one finding whose fix BEM will not apply, because the fix is
    // turning off somebody's mods and only the user knows which of them they want in this run. What
    // BEM owes them instead is the exact deficit and the ids that would pay it, which is what the
    // detail carries.
    private static void AddCommandLineLength(
        PreflightRequest request, List<PreflightFinding> findings, List<PreflightNote> notChecked)
    {
        if (request.Target is not { } target)
        {
            notChecked.Add(new PreflightNote(
                PreflightCheck.CommandLineLength,
                Strings.Current["Core.Launcher.Preflight.CommandLine.NotChecked"]));
            return;
        }

        var budget = GameCommandLine.Measure(target);

        if (budget.Fits && !budget.IsNearTheLimit)
            return;

        var watched = request.WatchIsArmed
            ? Strings.Current.Format(
                "Core.Launcher.Preflight.CommandLine.WatchNote", WatchedLaunch.CompanionCharacterCost)
            : string.Empty;

        findings.Add(new PreflightFinding(
            budget.Fits ? PreflightGrade.Advisory : PreflightGrade.Critical,
            PreflightCheck.CommandLineLength,
            budget.Fits
                ? Strings.Current.Plural("Core.Launcher.Preflight.CommandLine.Headroom", budget.Headroom)
                : Strings.Current.Plural("Core.Launcher.Preflight.CommandLine.Overflow", budget.Overflow),
            (budget.Fits
                ? Strings.Current.Format("Core.Launcher.Preflight.CommandLine.Detail.Fits", budget.Describe())
                : Strings.Current.Format("Core.Launcher.Preflight.CommandLine.Detail.Overflow", budget.Describe()))
            + DescribeLongestIds(request.Environment)
            + watched));
    }

    // Length of the id plus its separator, because that is exactly what leaves the command line when a
    // module is turned off. Cumulative, so the user can read straight off it how far down the list they
    // have to go to cover the deficit rather than doing the addition themselves.
    private static string DescribeLongestIds(ModuleEnvironment environment)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new List<string>();

        foreach (var entry in environment.Entries.Where(e => e.IsEnabled && !e.IsOrphan))
        {
            if (seen.Add(entry.Id.Value))
                ids.Add(entry.Id.Value);
        }

        var running = 0;

        return string.Join(", ", ids
            .OrderByDescending(id => id.Length)
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Take(LongestIdsShown)
            .Select(id =>
            {
                running += id.Length + 1;

                return Strings.Current.Format("Core.Launcher.Preflight.CommandLine.IdCost", id, id.Length + 1, running);
            }));
    }

    // The cheapest signal available and the only one that is about this exact run rather than about the
    // install in general. It says what happened, never why: BEM watched a process end, and an exit code
    // is not a diagnosis.
    private static string AddHistory(PreflightRequest request, List<PreflightFinding> findings)
    {
        if (request.History is null)
            return string.Empty;

        var order = LoadOrderSnapshot.From(request.Environment);
        var fingerprint = LoadOrderFingerprint.Of(order);
        var mine = request.History.Where(r => r.OrderFingerprint == fingerprint).ToList();

        if (mine.Count > 0)
            return DescribeSameOrder(mine[^1], findings);

        var lastClean = request.History.LastOrDefault(r => r.Outcome == LaunchOutcome.ExitedCleanly);

        if (lastClean is null)
        {
            return request.History.Count == 0
                ? Strings.Current["Core.Launcher.Preflight.History.NoRecord"]
                : Strings.Current["Core.Launcher.Preflight.History.NoCleanRun"];
        }

        AddChangesSince(lastClean, order, findings);

        return Strings.Current["Core.Launcher.Preflight.History.NotLaunchedBefore"];
    }

    private static string DescribeSameOrder(LaunchRecord last, List<PreflightFinding> findings)
    {
        var when = last.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        switch (last.Outcome)
        {
            case LaunchOutcome.ExitedCleanly:
                return Strings.Current.Format("Core.Launcher.Preflight.History.ExitedCleanly", when);

            case LaunchOutcome.Crashed:
                findings.Add(new PreflightFinding(
                    PreflightGrade.Warning,
                    PreflightCheck.LaunchHistory,
                    Strings.Current.Format("Core.Launcher.Preflight.History.CrashedHeadline", when),
                    Strings.Current.Format(
                        "Core.Launcher.Preflight.History.CrashedDetail",
                        $"{last.ExitCode:X8}",
                        Describe(last.Ran)),
                    null));

                return string.Empty;

            case LaunchOutcome.ExitedWithError:
                findings.Add(new PreflightFinding(
                    PreflightGrade.Advisory,
                    PreflightCheck.LaunchHistory,
                    Strings.Current.Format("Core.Launcher.Preflight.History.ExitedWithErrorHeadline", last.ExitCode, when),
                    Strings.Current["Core.Launcher.Preflight.History.ExitedWithErrorDetail"],
                    null));

                return string.Empty;

            default:
                return Strings.Current.Format("Core.Launcher.Preflight.History.DefaultLine", when, last.DescribeOutcome());
        }
    }

    // The "what did I just break" answer, and the reason the whole order is kept in the record rather
    // than only its fingerprint. It is a note and never a warning: a changed load order is what the
    // owner was doing on purpose.
    private static void AddChangesSince(
        LaunchRecord lastClean, LoadOrderSnapshot current, List<PreflightFinding> findings)
    {
        var difference = LoadOrderComparer.Compare(lastClean.Order, current);

        if (difference.IsIdentical)
            return;

        var names = difference.Changes
            .Select(c => c.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var shown = string.Join(", ", names.Take(NamesShown));
        var rest = names.Count > NamesShown
            ? Strings.Current.Format("Core.Launcher.Preflight.ChangesSince.AndMore", names.Count - NamesShown)
            : string.Empty;
        var when = lastClean.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        findings.Add(new PreflightFinding(
            PreflightGrade.Advisory,
            PreflightCheck.LaunchHistory,
            Strings.Current.Plural(
                "Core.Launcher.Preflight.ChangesSince.Headline", names.Count, when, difference.Summary),
            Strings.Current.Format("Core.Launcher.Preflight.ChangesSince.Detail", $"{shown}{rest}"),
            null));
    }

    private static string Describe(TimeSpan? ran) =>
        ran is { } length ? $"{length:hh\\:mm\\:ss}" : Strings.Current["Core.Launcher.Preflight.History.UnrecordedDuration"];
}
