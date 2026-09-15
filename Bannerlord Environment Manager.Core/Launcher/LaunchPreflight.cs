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
    // Something here stops a mod working. Not a prediction and not a heuristic: a hard dependency that
    // is not there, two modules that declare each other incompatible, an order no sort can satisfy.
    Critical,

    // Evidence that this run goes wrong, short of certainty.
    Warning,

    // True, worth knowing, and no reason on its own not to launch. Everything here lives behind a
    // disclosure and never in front of the Launch button.
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
    MissingPrerequisite
}

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
    bool Accepted = false)
{
    public bool IsFixable => Fix is not null;

    public bool IsProminent => Grade is PreflightGrade.Critical or PreflightGrade.Warning;

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
        [.. Findings.Where(f => f.IsProminent && !f.Accepted)];

    public IReadOnlyList<PreflightFinding> Advisories =>
        [.. Findings.Where(f => f.Grade == PreflightGrade.Advisory)];

    public int AcceptedCount => Findings.Count(f => f.IsProminent && f.Accepted);

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
        [.. Findings.Where(f => f.IsProminent && f.IsFixable && !f.IsDestructive && !f.Accepted)];

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
    IReadOnlySet<string>? AcceptedRiskKeys = null);

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

    public static PreflightReport Inspect(PreflightRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var findings = new List<PreflightFinding>();
        var notChecked = new List<PreflightNote>();

        var issues = LoadOrderValidator.Validate(request.Environment);
        var noteCount = issues.Count(i => i.Severity == IssueSeverity.Information);

        foreach (var issue in issues.Where(i => i.Severity != IssueSeverity.Information))
            findings.Add(FromLoadOrder(issue));

        AddMissingDeclaredAssemblies(request, findings);
        AddHarmonyPatchTargets(request, findings, notChecked);
        AddShadowedAssemblies(request, findings, notChecked);
        AddGameVersionGaps(request, findings, notChecked);
        AddRigConflicts(request, findings);
        AddSafety(request, findings, notChecked);
        AddCommandLineLength(request, findings, notChecked);
        AddEmptiedDatasets(request, findings, notChecked);
        AddMissingPrerequisites(request, findings);

        var history = AddHistory(request, findings);

        ApplyAcceptedRisks(request, findings);

        stopwatch.Stop();

        return new PreflightReport(
            [.. findings.OrderBy(f => f.Grade).ThenBy(f => f.Check)],
            notChecked,
            history,
            noteCount,
            stopwatch.Elapsed);
    }

    // Marking rather than filtering: every rule above still runs and still finds exactly what it would
    // have found with no acceptances on record, so Findings stays the complete, honest account of the
    // install. Only Prominent, the list a launch dialog reads, looks at Accepted at all.
    //
    // Critical is excluded on purpose, even if a stale or hand-edited store hands one in: it is not a
    // judgment call the user can override, it is what the loader does. Muting it would not stop the
    // game from failing before the main menu, it would only stop BEM from saying why the next time.
    private static void ApplyAcceptedRisks(PreflightRequest request, List<PreflightFinding> findings)
    {
        if (request.AcceptedRiskKeys is not { Count: > 0 } accepted)
            return;

        for (var i = 0; i < findings.Count; i++)
        {
            if (findings[i].Grade != PreflightGrade.Critical && accepted.Contains(findings[i].Key))
                findings[i] = findings[i] with { Accepted = true };
        }
    }

    // An Error from the validator is a module that does not work: a hard dependency missing, two
    // modules that declare each other incompatible, a loop no order satisfies. An unreadable manifest
    // and a duplicated id are the two warnings the game itself acts on, by skipping the module and by
    // picking one folder. Everything else the validator calls a warning is about where a module sits
    // relative to another, which nothing in the engine enforces, so it is advice.
    private static PreflightFinding FromLoadOrder(LoadOrderIssue issue) => new(
        issue.Severity == IssueSeverity.Error
            ? PreflightGrade.Critical
            : issue.Kind is IssueKind.UnreadableManifest or IssueKind.DuplicateModuleId
                ? PreflightGrade.Warning
                : PreflightGrade.Advisory,
        PreflightCheck.LoadOrder,
        issue.Message,
        DetailFor(issue),
        issue.ModuleId,
        issue.Fix,
        FixLabelFor(issue.Kind),
        issue.IsDestructive);

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
            request.Environment.Entries, request.LoadedPlatformFolder))
        {
            findings.Add(new PreflightFinding(
                PreflightGrade.Critical,
                PreflightCheck.MissingDeclaredAssembly,
                missing.Describe(),
                Strings.Current["Core.Launcher.Preflight.MissingAssembly.Detail"],
                missing.ModuleId));
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
    private static void AddHarmonyPatchTargets(
        PreflightRequest request, List<PreflightFinding> findings, List<PreflightNote> notChecked)
    {
        if (request.Assemblies is null)
        {
            notChecked.Add(new PreflightNote(
                PreflightCheck.HarmonyPatchTarget,
                Strings.Current["Core.Launcher.Preflight.Harmony.NotIndexed"]));
            return;
        }

        if (request.Assemblies.Failed)
        {
            notChecked.Add(new PreflightNote(
                PreflightCheck.HarmonyPatchTarget,
                Strings.Current.Format("Core.Launcher.Preflight.Harmony.CouldNotRead", request.Assemblies.Error)));
            return;
        }

        var report = HarmonyPatchTargets.Inspect(request.Assemblies, request.Environment.Entries);

        foreach (var problem in report.Problems)
        {
            notChecked.Add(new PreflightNote(
                PreflightCheck.HarmonyPatchTarget,
                $"{problem.Path}: {problem.Reason}"));
        }

        foreach (var target in report.Findings)
        {
            var missingType = target.Verdict
                is HarmonyTargetVerdict.TypeMissing
                or HarmonyTargetVerdict.AssemblyNotInstalled
                or HarmonyTargetVerdict.AssemblyNotEnabled;

            findings.Add(new PreflightFinding(
                missingType ? PreflightGrade.Critical : PreflightGrade.Warning,
                PreflightCheck.HarmonyPatchTarget,
                target.Describe(),
                missingType
                    ? Strings.Current["Core.Launcher.Preflight.Harmony.Detail.TypeMissing"]
                    : Strings.Current["Core.Launcher.Preflight.Harmony.Detail.MemberMissing"],
                target.ModuleId));
        }
    }

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

        foreach (var shadowed in found)
        {
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
                newer[0].Copy.ModuleId));
        }
    }

    // Every one of these fires on a healthy real install: five modules ship per-game-version
    // assemblies and none of them has a build for the game that is installed, and the game runs. So
    // the direction is what decides the grade. A game newer than everything a mod ships is the normal
    // state of a modded install a few days after a patch, and the loader falls back to the newest
    // build the mod has. A game OLDER than everything it ships is the other case: the mod was built
    // against engine methods this game does not have yet, and nothing falls back to those.
    //
    // Disproof condition: an install where a game-is-newer gap is what actually took the run down. If
    // that turns up, the split is wrong and both directions belong in front of the Launch button.
    private static void AddGameVersionGaps(
        PreflightRequest request, List<PreflightFinding> findings, List<PreflightNote> notChecked)
    {
        if (request.GameVersion.IsEmpty)
        {
            notChecked.Add(new PreflightNote(
                PreflightCheck.GameVersion,
                Strings.Current["Core.Launcher.Preflight.GameVersion.NotChecked"]));
            return;
        }

        foreach (var gap in GameVersionSupport.Inspect(request.Environment.Entries, request.GameVersion).Gaps)
        {
            findings.Add(new PreflightFinding(
                gap.GameIsNewer ? PreflightGrade.Advisory : PreflightGrade.Warning,
                PreflightCheck.GameVersion,
                gap.Headline,
                gap.Why,
                gap.Set.ModuleId));
        }
    }

    // Critical without qualification. A dataset with nothing left in it is not a balance change or a
    // conflict between two mods, it is the game reading an empty list where it expects its own content,
    // and whatever breaks does so long after the module responsible has finished loading.
    private static void AddEmptiedDatasets(
        PreflightRequest request, List<PreflightFinding> findings, List<PreflightNote> notChecked)
    {
        if (request.Transforms is not { } transforms)
        {
            notChecked.Add(new PreflightNote(
                PreflightCheck.DatasetEmptiedByStylesheet,
                Strings.Current["Core.Launcher.Preflight.EmptiedDataset.NotChecked"]));
            return;
        }

        foreach (var transform in transforms.Where(t => t.EmptiesDataset))
        {
            findings.Add(new PreflightFinding(
                PreflightGrade.Critical,
                PreflightCheck.DatasetEmptiedByStylesheet,
                Strings.Current.Format(
                    "Core.Launcher.Preflight.EmptiedDataset.Headline", transform.DisplayName, transform.Dataset),
                transform.Describe(),
                transform.ModuleId));
        }
    }

    // Only reported when something enabled actually declares it. A setup with no mod needing ButterLib
    // is not missing ButterLib, and saying so would put a permanent complaint in front of a launch that
    // works, which is how a preflight gets ignored.
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
                PreflightGrade.Critical,
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
                result.Verdict == SafetyVerdict.KnownBad ? PreflightGrade.Warning : PreflightGrade.Advisory,
                PreflightCheck.ModSafety,
                Strings.Current.Format(
                    "Core.Launcher.Preflight.Safety.Headline", entry.DisplayName, result.VerdictText),
                Strings.Current.Format("Core.Launcher.Preflight.Safety.Detail", result.VerdictExplanation),
                entry.Id));
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
