using System.Globalization;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.DryRun;

public enum TimelineSeverity
{
    // The run doing what a run does. Kept, because the value of a timeline is the ordinary events
    // either side of the interesting one, but not shown first.
    Normal,

    // Worth a reader's attention on its own: an exception something handled, a phase boundary.
    Notable,

    // Something threw where the trail can see it throw.
    Failure
}

public sealed record TimelineEvent(
    DateTime WhenUtc,
    long? AtMs,
    string Headline,
    string Detail,
    string ModuleId,
    TimelineSeverity Severity)
{
    public string At => WhenUtc == default
        ? string.Empty
        : WhenUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture);
}

public sealed record ModulePatchSummary(ModuleId ModuleId, int Patches, int Methods);

// The ordered account of one launch, from the companion attaching to whatever ended it.
//
// The whole point is the line that names what started and never finished. A minidump of a crash
// inside Harmony.PatchAll names Harmony and it names the game, and the frames in between are JIT
// code the dump has no memory for, so it cannot name the mod. A trail that says "Bannerlord.X
// started applying its Harmony patches and never finished" names it in one line and needs no dump.
//
// Observed and Implied are separate strings, deliberately, and neither is allowed to say the other's
// job. "X started and never finished" is observation. "X is why the game died" is not, because a
// module can fail on state another module left behind, and this project has been burned repeatedly
// by a confident wrong name.
public sealed record LaunchTimeline(
    string RunId,
    string Mode,
    bool CompanionAttached,
    IReadOnlyList<TimelineEvent> Events,
    IReadOnlyList<TimelineEvent> Highlights,
    string Headline,
    string Observed,
    string Implied,
    string? UnfinishedModule,
    string? UnfinishedPatchScan,
    string? UnfinishedPatchClass,
    bool ReachedFirstTick,
    IReadOnlyList<ModulePatchSummary> PatchesByModule)
{
    public static LaunchTimeline NotLooked(string runId) => new(
        runId,
        string.Empty,
        false,
        [],
        [],
        Strings.Current["Core.DryRun.Timeline.NotLooked.Headline"],
        Strings.Current["Core.DryRun.Timeline.NotLooked.Observed"],
        Strings.Current["Core.DryRun.Timeline.NotLooked.Implied"],
        null,
        null,
        null,
        false,
        []);

    public bool Failed => UnfinishedModule is not null || UnfinishedPatchScan is not null
        || Events.Any(e => e.Severity == TimelineSeverity.Failure);
}

public sealed record LaunchRun(string RunId, DateTime WhenUtc, string BreadcrumbPath, string ResultPath, string TracePath);

// Finds the launches BEM recorded, newest first.
//
// One armed watch produces one run per launch, not one run in total: the companion derives a run id
// per process from the armed one, so a session started from Steam an hour later is its own trail
// rather than an overwrite of the first. That is why this reads the folder rather than the state
// file's single id.
public static class LaunchRunLocator
{
    public static IReadOnlyList<LaunchRun> List(string runRoot, string traceRoot, int limit = 25)
    {
        try
        {
            if (!Directory.Exists(runRoot))
                return [];

            return
            [
                .. Directory.EnumerateFiles(runRoot, "*" + DryRunPaths.BreadcrumbSuffix)
                    .Select(path => new LaunchRun(
                        Path.GetFileName(path)[..^DryRunPaths.BreadcrumbSuffix.Length],
                        File.GetLastWriteTimeUtc(path),
                        path,
                        DryRunPaths.ResultPath(runRoot, Path.GetFileName(path)[..^DryRunPaths.BreadcrumbSuffix.Length]),
                        FirstChancePaths.TracePath(traceRoot, Path.GetFileName(path)[..^DryRunPaths.BreadcrumbSuffix.Length])))
                    .OrderByDescending(run => run.WhenUtc)
                    .Take(limit)
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    // Everything the timeline needs for one run, read off disk. Missing pieces stay missing rather
    // than being invented: a run whose result file was never written still has a trail worth reading.
    public static LaunchTimeline Read(LaunchRun run) => LaunchTimelineBuilder.Build(
        run.RunId,
        BreadcrumbFile.Read(run.BreadcrumbPath),
        FirstChanceTrace.Read(run.TracePath),
        DryRunResultFile.Read(run.ResultPath));

    public static LaunchTimeline? ReadNewest(string runRoot, string traceRoot)
    {
        var runs = List(runRoot, traceRoot, 1);

        return runs.Count == 0 ? null : Read(runs[0]);
    }
}

public static class LaunchTimelineBuilder
{
    // Enough context around the interesting rows to be readable, and few enough rows that the screen
    // is not the wall of text this feature is most at risk of becoming.
    private const int SlowestModulesShown = 3;

    public static LaunchTimeline Build(
        string runId,
        BreadcrumbTrail? trail,
        FirstChanceTrace? trace,
        DryRunResult? result)
    {
        trail ??= BreadcrumbTrail.Empty;

        if (trail.Records.Count == 0 && trace is null && result is null)
            return LaunchTimeline.NotLooked(runId);

        // The breadcrumb is kept beside the row it produced so the highlights can be chosen from what
        // the record says rather than from the sentence it was rendered into.
        var events = new List<(Breadcrumb? Record, TimelineEvent Event)>();

        foreach (var record in trail.Records)
        {
            if (Render(record) is { } rendered)
                events.Add((record, rendered));
        }

        foreach (var record in trace?.Records ?? [])
            events.Add((null, Render(record)));

        // Stable inside a tie: a breadcrumb and an exception written in the same millisecond keep the
        // order they were read in, which is the order they happened in.
        var ordered = events.OrderBy(e => e.Event.WhenUtc).ToList();

        var unfinishedScan = trail.UnfinishedPatchScan;
        var patchClass = LastPatchClass(trail);

        var timeline = new LaunchTimeline(
            runId,
            trail.Mode,
            true,
            [.. ordered.Select(e => e.Event)],
            Highlights(ordered, trail),
            string.Empty,
            string.Empty,
            string.Empty,
            trail.LoadingModule,
            unfinishedScan?.PatchLabel is { Length: > 0 } label ? label : null,
            patchClass,
            trail.LastPhase is "first-tick" or "shutdown" || result?.ReachedFirstTick == true,
            PatchesByModule(result));

        // The last thing BEM saw, of anything: a breadcrumb, a heartbeat, an exception. Meaningful
        // only alongside a heartbeat, because without one this is just the first-tick timestamp from
        // however long ago the session started, and saying "still running as of the menu loading" is
        // not what it looks like it is saying.
        DateTime? lastEventUtc = ordered.Count > 0 ? ordered[^1].Event.WhenUtc : null;

        return timeline with
        {
            Headline = BuildHeadline(timeline, trail, lastEventUtc),
            Observed = BuildObserved(timeline, trail, trace, lastEventUtc),
            Implied = BuildImplied(timeline, trail)
        };
    }

    private static string? LastPatchClass(BreadcrumbTrail trail)
    {
        for (var i = trail.Records.Count - 1; i >= 0; i--)
        {
            var record = trail.Records[i];

            if (record.PatchClass.Length > 0)
                return record.PatchClass;
        }

        return null;
    }

    private static IReadOnlyList<ModulePatchSummary> PatchesByModule(DryRunResult? result)
    {
        var patches = result?.Patches;

        if (patches is null || patches.Count == 0)
            return [];

        return
        [
            .. patches
                .Where(p => !p.ModuleId.IsEmpty)
                .GroupBy(p => p.ModuleId)
                .Select(g => new ModulePatchSummary(
                    g.Key,
                    g.Count(),
                    g.Select(p => p.TargetTypeFullName + "." + p.TargetMethodName).Distinct(StringComparer.Ordinal).Count()))
                .OrderByDescending(s => s.Patches)
                .ThenBy(s => s.ModuleId.Value, StringComparer.OrdinalIgnoreCase)
        ];
    }

    // Null for anything whose own row would say nothing a reader wants: the trail keeps it, the
    // screen does not.
    private static TimelineEvent? Render(Breadcrumb record) => record.Kind switch
    {
        "run-start" => Event(
            record, Strings.Current["Core.DryRun.Timeline.Event.RunStart"], string.Empty, TimelineSeverity.Notable),
        "discovery" => Event(
            record,
            Strings.Current.Plural("Core.DryRun.Timeline.Event.Discovery", record.SubModuleCount ?? 0),
            record.DegradedReason,
            TimelineSeverity.Normal),
        "patch-watch" => Event(
            record,
            Strings.Current["Core.DryRun.Timeline.Event.PatchWatch"],
            record.DegradedReason,
            TimelineSeverity.Normal),
        "begin" => Event(
            record,
            Strings.Current.Format("Core.DryRun.Timeline.Event.Begin", record.Label),
            record.TypeName,
            TimelineSeverity.Normal),
        "end" => Event(
            record,
            Strings.Current.Format("Core.DryRun.Timeline.Event.End", record.Label, record.DurationMs ?? 0),
            record.TypeName,
            TimelineSeverity.Normal),
        "throw" => Event(
            record,
            Strings.Current.Format("Core.DryRun.Timeline.Event.Throw", record.Label),
            Join(record.ExceptionType, record.ExceptionMessage, record.TypeName),
            TimelineSeverity.Failure),
        "patch-scan-begin" => Event(
            record,
            Strings.Current.Format("Core.DryRun.Timeline.Event.PatchScanBegin", record.PatchLabel),
            Join(
                Strings.Current.Format("Core.DryRun.Timeline.Detail.Assembly", record.Assembly),
                Strings.Current.Format("Core.DryRun.Timeline.Detail.HarmonyId", record.HarmonyId)),
            TimelineSeverity.Normal),
        "patch-scan-end" => Event(
            record,
            Strings.Current.Format("Core.DryRun.Timeline.Event.PatchScanEnd", record.PatchLabel),
            Join(
                Strings.Current.Format("Core.DryRun.Timeline.Detail.Assembly", record.Assembly),
                Strings.Current.Format("Core.DryRun.Timeline.Detail.HarmonyId", record.HarmonyId)),
            TimelineSeverity.Normal),
        "patch-scan-throw" => Event(
            record,
            Strings.Current.Format("Core.DryRun.Timeline.Event.PatchScanThrow", record.PatchLabel),
            Join(
                record.ExceptionType,
                record.ExceptionMessage,
                record.PatchClass.Length > 0
                    ? Strings.Current.Format("Core.DryRun.Timeline.Detail.LastPatchClass", record.PatchClass)
                    : string.Empty,
                Strings.Current.Format("Core.DryRun.Timeline.Detail.HarmonyId", record.HarmonyId)),
            TimelineSeverity.Failure),
        "patch-class-throw" => Event(
            record,
            Strings.Current.Format("Core.DryRun.Timeline.Event.PatchClassThrow", record.PatchLabel, record.PatchClass),
            Join(record.ExceptionType, record.ExceptionMessage),
            TimelineSeverity.Failure),
        "patch-manual-begin" => Event(
            record,
            Strings.Current.Format("Core.DryRun.Timeline.Event.PatchManualBegin", record.PatchLabel),
            Join(
                Strings.Current.Format("Core.DryRun.Timeline.Detail.HarmonyId", record.HarmonyId),
                Strings.Current.Format("Core.DryRun.Timeline.Detail.FirstTarget", record.Target)),
            TimelineSeverity.Normal),
        "patch-manual-throw" => Event(
            record,
            Strings.Current.Format("Core.DryRun.Timeline.Event.PatchManualThrow", record.PatchLabel, record.Target),
            Join(
                record.ExceptionType,
                record.ExceptionMessage,
                Strings.Current.Format("Core.DryRun.Timeline.Detail.HarmonyId", record.HarmonyId)),
            TimelineSeverity.Failure),
        "patch-watch-capped" => Event(
            record,
            Strings.Current["Core.DryRun.Timeline.Event.PatchWatchCapped"],
            string.Empty,
            TimelineSeverity.Notable),
        "patch-registry" => Event(
            record, Strings.Current["Core.DryRun.Timeline.Event.PatchRegistry"], string.Empty, TimelineSeverity.Normal),
        // A trail whose last line is this one died while the companion was rewriting methods, which is
        // the one window where a failure ends the process instead of throwing.
        "patch-install-begin" => Event(
            record,
            Strings.Current["Core.DryRun.Timeline.Event.PatchInstallBegin"],
            string.Empty,
            TimelineSeverity.Normal),
        "phase" => RenderPhase(record),
        "capture-failed" => Event(
            record,
            Strings.Current["Core.DryRun.Timeline.Event.CaptureFailed"],
            record.DegradedReason,
            TimelineSeverity.Notable),
        "exit" => Event(
            record, Strings.Current["Core.DryRun.Timeline.Event.Exit"], string.Empty, TimelineSeverity.Notable),
        "heartbeat" => Event(
            record, Strings.Current["Core.DryRun.Timeline.Event.Heartbeat"], string.Empty, TimelineSeverity.Normal),
        _ => null
    };

    private static TimelineEvent RenderPhase(Breadcrumb record) => record.Phase switch
    {
        "before-initial-screen" => Event(
            record,
            Strings.Current["Core.DryRun.Timeline.Phase.BeforeInitialScreen"],
            string.Empty,
            TimelineSeverity.Notable),
        "first-tick" => Event(
            record, Strings.Current["Core.DryRun.Timeline.Phase.FirstTick"], string.Empty, TimelineSeverity.Notable),
        "shutdown" => Event(
            record, Strings.Current["Core.DryRun.Timeline.Phase.Shutdown"], string.Empty, TimelineSeverity.Notable),
        _ => Event(
            record,
            Strings.Current.Format("Core.DryRun.Timeline.Phase.Default", record.Phase),
            string.Empty,
            TimelineSeverity.Normal)
    };

    private static TimelineEvent Render(FirstChanceRecord record)
    {
        var who = record.Module.IsEmpty
            ? string.Empty
            : Strings.Current.Format("Core.DryRun.Timeline.FirstChance.From", record.Module);
        var again = record.Repeats > 1
            ? Strings.Current.Format("Core.DryRun.FirstChance.Describe.Repeats", record.Repeats)
            : string.Empty;

        var headline = record.ThrownWhilePatching
            ? record.PatchingModule.IsEmpty
                ? Strings.Current.Format(
                    "Core.DryRun.Timeline.FirstChance.ThrownWhilePatching", record.ExceptionType, record.PatchingActivity, again)
                : Strings.Current.Format(
                    "Core.DryRun.Timeline.FirstChance.ThrownWhileModulePatching",
                    record.ExceptionType, record.PatchingModule, record.PatchingActivity, again)
            : Strings.Current.Format("Core.DryRun.Timeline.FirstChance.Thrown", record.ExceptionType, who, again);

        return new TimelineEvent(
            record.WhenUtc,
            null,
            headline,
            Join(record.Message, string.Join("\n", record.Frames)),
            record.PatchingModule.IsEmpty ? record.Module.Value : record.PatchingModule.Value,
            TimelineSeverity.Notable);
    }

    private static TimelineEvent Event(Breadcrumb record, string headline, string detail, TimelineSeverity severity) =>
        new(record.TimestampUtc, record.AtMs, headline, detail, record.ModuleId, severity);

    private static string Join(params string?[] parts) =>
        string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    // What a person reads first. Everything that failed, every phase boundary, every handled
    // exception, the module that never finished, and the three slowest modules so a run that did not
    // fail still says something useful.
    private static IReadOnlyList<TimelineEvent> Highlights(
        IReadOnlyList<(Breadcrumb? Record, TimelineEvent Event)> ordered,
        BreadcrumbTrail trail)
    {
        // By sequence number, which is unique per line. Timestamps are not: a hundred modules can
        // finish inside the same millisecond, and keying on the clock silently promoted every one of
        // them to a highlight.
        var slowest = trail.Records
            .Where(r => r.Kind == "end" && r.DurationMs is > 0)
            .OrderByDescending(r => r.DurationMs)
            .Take(SlowestModulesShown)
            .Select(r => r.Sequence)
            .ToHashSet();

        return
        [
            .. ordered
                .Where(e => e.Event.Severity != TimelineSeverity.Normal
                    || (e.Record is { } record && slowest.Contains(record.Sequence)))
                .Select(e => e.Event)
        ];
    }

    // A heartbeat only exists in Watch mode, and only after the first tick, so its presence is what
    // tells a real, ongoing session apart from one whose trail is simply short. Its absence never
    // means anything on its own: a session five seconds long has none to give.
    private static bool StoppedWithoutClosing(BreadcrumbTrail trail) => trail.HeartbeatCount > 0 && !trail.ExitRecorded;

    private static string FormatLocal(DateTime utc) =>
        utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    private static string BuildHeadline(LaunchTimeline timeline, BreadcrumbTrail trail, DateTime? lastEventUtc)
    {
        if (timeline.UnfinishedPatchScan is { } scan)
            return Strings.Current.Format("Core.DryRun.Timeline.Headline.UnfinishedPatchScan", scan);

        if (timeline.UnfinishedModule is { } module)
            return Strings.Current.Format("Core.DryRun.Timeline.Headline.UnfinishedModule", module);

        if (trail.Failures.Count > 0)
            return Strings.Current.Plural("Core.DryRun.Timeline.Headline.ModulesThrew", trail.Failures.Count);

        if (timeline.Events.Any(e => e.Severity == TimelineSeverity.Failure))
            return Strings.Current["Core.DryRun.Timeline.Headline.ThrewDuringPatching"];

        if (!timeline.ReachedFirstTick)
            return Strings.Current["Core.DryRun.Timeline.Headline.NoFirstTick"];

        // The ordinary "everything finished" headline is true but silent about what happened for
        // however long the player then played, which is exactly the question a hang leaves open. A
        // heartbeat with nothing after it and no recorded exit says the trail simply stops, not that
        // the session was short or uneventful. It does not say the game is gone: reading this while
        // it is still running, mid-session, looks exactly the same, and the headline says so rather
        // than picking one.
        return StoppedWithoutClosing(trail) && lastEventUtc is { } last
            ? Strings.Current.Format("Core.DryRun.Timeline.Headline.StoppedWithoutClosing", FormatLocal(last))
            : Strings.Current["Core.DryRun.Timeline.Headline.Clean"];
    }

    private static string BuildObserved(
        LaunchTimeline timeline, BreadcrumbTrail trail, FirstChanceTrace? trace, DateTime? lastEventUtc)
    {
        var lines = new List<string>();

        if (timeline.UnfinishedPatchScan is { } scan)
        {
            lines.Add(Strings.Current.Format("Core.DryRun.Timeline.Observed.UnfinishedPatchScan", scan)
                + (timeline.UnfinishedPatchClass is { } patchClass
                    ? Strings.Current.Format("Core.DryRun.Timeline.Observed.LastPatchClass", patchClass)
                    : string.Empty));
        }

        if (timeline.UnfinishedModule is { } module)
            lines.Add(Strings.Current.Format("Core.DryRun.Timeline.Observed.UnfinishedModule", module));

        foreach (var failure in trail.Failures)
        {
            lines.Add(Strings.Current.Format(
                "Core.DryRun.Timeline.Observed.Failure.Base",
                failure.Label,
                failure.ExceptionType ?? Strings.Current["Core.DryRun.Timeline.Observed.UnknownExceptionType"])
                + (string.IsNullOrWhiteSpace(failure.ExceptionMessage)
                    ? "."
                    : Strings.Current.Format("Core.DryRun.Timeline.Observed.Failure.WithMessage", failure.ExceptionMessage)));
        }

        var whilePatching = trace?.Records.Where(r => r.ThrownWhilePatching).ToList() ?? [];

        if (whilePatching.Count > 0)
        {
            lines.Add(Strings.Current.Plural(
                "Core.DryRun.Timeline.Observed.WhilePatching", whilePatching.Count, whilePatching[^1].Describe()));
        }

        if (trail.DiscardedLines > 0)
            lines.Add(Strings.Current.Plural("Core.DryRun.Timeline.Observed.DiscardedLines", trail.DiscardedLines));

        if (lines.Count == 0)
        {
            var baseline = trail.SubModuleCount is { } count
                ? Strings.Current.Plural("Core.DryRun.Timeline.Observed.Baseline.WithCount", count)
                : Strings.Current["Core.DryRun.Timeline.Observed.Baseline.NoCount"];

            lines.Add(StoppedWithoutClosing(trail) && lastEventUtc is { } last
                ? Strings.Current.Format("Core.DryRun.Timeline.Observed.StoppedWithoutClosing", baseline, FormatLocal(last))
                : baseline);
        }

        return string.Join(" ", lines);
    }

    private static string BuildImplied(LaunchTimeline timeline, BreadcrumbTrail trail)
    {
        var lines = new List<string>();

        if (timeline.UnfinishedPatchScan is not null || timeline.UnfinishedModule is not null)
            lines.Add(Strings.Current["Core.DryRun.Timeline.Implied.WhereItStopped"]);

        if (trail.Failures.Count > 0)
            lines.Add(Strings.Current["Core.DryRun.Timeline.Implied.ThrewAndCarriedOn"]);

        if (trail.IsDegraded && trail.DegradedReason.Length > 0)
            lines.Add(trail.DegradedReason);

        // Only reached when nothing above already explains the trail's end: no unfinished scan or
        // module, no observed failure. A heartbeat with no exit is the one remaining fact worth
        // stating, and it names what it does not know as plainly as what it does.
        if (lines.Count == 0 && StoppedWithoutClosing(trail))
            lines.Add(Strings.Current["Core.DryRun.Timeline.Implied.StoppedWithoutClosing"]);

        if (lines.Count == 0)
            lines.Add(Strings.Current["Core.DryRun.Timeline.Implied.NothingExplainsIt"]);

        return string.Join(" ", lines);
    }
}
