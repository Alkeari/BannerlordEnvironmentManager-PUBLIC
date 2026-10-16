using System.Globalization;
using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public enum RunEvidenceKind
{
    // A mod said it could not find a build matching the game, and loaded a different one anyway.
    VersionFallback,

    // The game's own log recorded a fault it did not survive.
    UnhandledException,

    // The game's own log recorded why no crash report was produced.
    NoCrashReport,

    // The newest thing anything wrote, which is how far the run demonstrably got.
    LastActivity,

    // The capture ran and copied nothing, which is a result rather than an absence of one.
    NothingCaptured
}

public sealed record RunEvidenceFinding(
    RunEvidenceKind Kind,
    string Headline,
    string Detail,
    string Why,
    string Owner = "",
    string SourcePath = "",
    // What the finding is about, when that is narrower than the line it was read from. ButterLib writes
    // the same fallback into its own log and into the shared sink, and one event logged twice is one
    // event: without this the same thing is counted as two findings under two different writers.
    string Subject = "")
{
    public (RunEvidenceKind Kind, string Subject) Identity => (Kind, Subject.Length > 0 ? Subject : Detail);
}

// What one captured run actually left behind, read out of BEM's own store. Every number here is
// observed: a file that was copied, a line that was written, a timestamp that is in the file. Nothing
// here names a culprit, and nothing here claims how long a process lived, because the store holds no
// record of that.
public sealed record RunEvidenceReport(
    string RunId,
    string Reason,
    string FolderPath,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    int LogCount,
    int CrashArtifactCount,
    DateTimeOffset? FirstEntry,
    DateTimeOffset? LastEntry,
    IReadOnlyList<RunEvidenceFinding> Findings,
    IReadOnlyList<string> Unreadable,
    string Summary)
{
    // The run's own clock, from the first line anything logged to the last. This is the figure a dry
    // run's elapsed time is not: it comes from the files, not from a process handle BEM happened to
    // hold, and a launcher that hands the game to another process cannot shorten it.
    public TimeSpan? ObservedSpan => FirstEntry is { } first && LastEntry is { } last && last >= first
        ? last - first
        : null;

    public string Title
    {
        get
        {
            var when = StartedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

            return Reason.Length > 0
                ? Strings.Current.Format("Core.Diagnostics.RunEvidence.Title.WithReason", Reason, when)
                : Strings.Current.Format("Core.Diagnostics.RunEvidence.Title.NoReason", RunId, when);
        }
    }
}

public static partial class RunEvidence
{
    private const int MaxLines = 200000;
    private const int MaxLineLength = 500;
    private const int FallbackWindow = 5;

    // The generalized form of what a loader writes when it cannot find a build for the game it is in.
    // Derived from a real line, "Found no matching implementations. Loading the latest available", and
    // deliberately not tied to it: the evidence is a negation next to a word about matching, followed
    // within a few lines by a versioned assembly being chosen. No mod name and no version appears here.
    [GeneratedRegex(@"\b(?:found\s+no|no|not|none|never)\b[^\r\n]{0,40}?\b(?:match|matches|matching|matched|compatible|suitable|supported)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex NoMatch { get; }

    [GeneratedRegex(@"\b(?:load|loads|loaded|loading|use|uses|using|select|selects|selected|fall(?:ing|s)?\s+back|fallback|instead|latest available)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Chose { get; }

    [GeneratedRegex(@"\b(?<file>[\w.]+?\.\d+\.\d+\.\d+(?:\.\d+)?\.dll)\b", RegexOptions.IgnoreCase)]
    private static partial Regex VersionedAssembly { get; }

    // Who wrote the line, when the logger stamps itself in brackets the way ButterLib and Diplomacy do.
    [GeneratedRegex(@"\[(?<owner>[A-Za-z][\w]*(?:\.[\w]+)+)\]")]
    private static partial Regex BracketedOwner { get; }

    [GeneratedRegex(@"\bunhandled\s+exception\b", RegexOptions.IgnoreCase)]
    private static partial Regex Unhandled { get; }

    // Why a crash left no report. The game writes its dump before its uploader runs, so a dump that was
    // never generated is the whole reason a crash folder is missing, rather than one BEM failed to copy.
    [GeneratedRegex(@"\bdump\b[^\r\n]{0,60}?\b(?:cancell?ed|aborted|skipped|failed|disabled)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DumpNotWritten { get; }

    [GeneratedRegex(@"^\[(?<stamp>[^\]]{7,40})\]")]
    private static partial Regex BracketedStamp { get; }

    [GeneratedRegex(@"^(?<stamp>\d{1,2}:\d{2}:\d{2}(?:[.,]\d+)?)\b")]
    private static partial Regex BareTime { get; }

    private static readonly string[] TextExtensions = [".log", ".txt"];

    private static readonly string[] CrashLabels = ["crashes", "butterlib-crashes", "crashdoctor"];

    // Reads one capture. onDisk is optional and only ever adds: a fallback line found in a log is
    // reported whether or not BEM can also see the assemblies on disk, and saying which of those two it
    // has is the difference between "found nothing" and "could not look".
    public static RunEvidenceReport Read(ArtifactCapture capture, GameVersionSupportReport? onDisk = null)
    {
        ArgumentNullException.ThrowIfNull(capture);

        var findings = new List<RunEvidenceFinding>();
        var unreadable = new List<string>(capture.Unreadable);
        var logs = 0;
        var crashArtifacts = 0;

        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        var lastLine = string.Empty;
        var lastFrom = string.Empty;

        var localDate = DateOnly.FromDateTime(capture.StartedUtc.ToLocalTime().DateTime);

        foreach (var artifact in capture.Artifacts)
        {
            if (CrashLabels.Contains(artifact.Label, StringComparer.OrdinalIgnoreCase)
                || Path.GetExtension(artifact.CapturedPath).Equals(".dmp", StringComparison.OrdinalIgnoreCase))
            {
                crashArtifacts++;
                continue;
            }

            if (!TextExtensions.Contains(Path.GetExtension(artifact.CapturedPath), StringComparer.OrdinalIgnoreCase))
                continue;

            logs++;

            var read = ReadOne(artifact, capture.StartedUtc, localDate, onDisk, findings, unreadable);

            if (read.First is { } startedAt && (first is null || startedAt < first))
                first = startedAt;

            if (read.Last is not { } endedAt || (last is not null && endedAt <= last))
                continue;

            last = endedAt;
            lastLine = read.LastLine;
            lastFrom = artifact.OriginalPath;
        }

        // The same fault written into two of the game's own log files is one fault. Keeping both would
        // read as two crashes, which is a false count on the one row nobody can afford to doubt.
        var deduplicated = findings
            .GroupBy(f => f.Identity)
            .Select(g => g.First())
            .ToList();

        if (last is { } newest && lastLine.Length > 0)
        {
            deduplicated.Add(new RunEvidenceFinding(
                RunEvidenceKind.LastActivity,
                Strings.Current.Format(
                    "Core.Diagnostics.RunEvidence.LastActivity.Headline",
                    newest.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)),
                lastLine,
                Strings.Current.Plural("Core.Diagnostics.RunEvidence.LastActivity.Why", logs),
                string.Empty,
                lastFrom));
        }

        // The sentence below exists to draw the line between "found nothing" and "could not look", so it
        // has to know which side it is on. Anything the capture could not read puts it on the other side,
        // and claiming BEM looked when a folder refused it is the exact mistake the sentence guards.
        if (logs == 0 && crashArtifacts == 0)
        {
            deduplicated.Insert(0, new RunEvidenceFinding(
                RunEvidenceKind.NothingCaptured,
                unreadable.Count == 0
                    ? Strings.Current["Core.Diagnostics.RunEvidence.NothingCaptured.Headline.Clean"]
                    : Strings.Current["Core.Diagnostics.RunEvidence.NothingCaptured.Headline.Unreadable"],
                Strings.Current.Format(
                    "Core.Diagnostics.RunEvidence.NothingCaptured.Detail",
                    capture.StartedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    (capture.FinishedUtc ?? capture.StartedUtc).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    capture.FolderPath),
                unreadable.Count == 0
                    ? Strings.Current["Core.Diagnostics.RunEvidence.NothingCaptured.Why.Clean"]
                    : Strings.Current.Format(
                        "Core.Diagnostics.RunEvidence.NothingCaptured.Why.Unreadable",
                        unreadable.Count, string.Join("; ", unreadable)),
                string.Empty,
                capture.FolderPath));
        }

        return new RunEvidenceReport(
            capture.RunId,
            capture.Reason,
            capture.FolderPath,
            capture.StartedUtc,
            capture.FinishedUtc,
            logs,
            crashArtifacts,
            first,
            last,
            deduplicated,
            unreadable,
            Summarize(logs, crashArtifacts, first, last, deduplicated, unreadable.Count));
    }

    // Every capture in the store, newest first. The run the user cares about is often not the run BEM
    // last started: a boot check started after a crash is its own run, and reporting only on that one is
    // how a session that crashed came to be answered with a summary of something else.
    public static IReadOnlyList<RunEvidenceReport> ReadStore(
        string root, int take, GameVersionSupportReport? onDisk = null) =>
        [.. ArtifactCaptureStore.List(root).Take(Math.Max(0, take)).Select(capture => Read(capture, onDisk))];

    private static string Summarize(
        int logs,
        int crashArtifacts,
        DateTimeOffset? first,
        DateTimeOffset? last,
        IReadOnlyList<RunEvidenceFinding> findings,
        int unreadable)
    {
        if (logs == 0 && crashArtifacts == 0)
        {
            return unreadable == 0
                ? Strings.Current["Core.Diagnostics.RunEvidence.Summary.NothingClean"]
                : Strings.Current.Plural("Core.Diagnostics.RunEvidence.Summary.NothingUnreadable", unreadable);
        }

        var parts = new List<string>
        {
            Strings.Current.Format("Core.Diagnostics.RunEvidence.Summary.Captured", logs, crashArtifacts)
        };

        if (first is { } from && last is { } to && to >= from)
        {
            parts.Add(Strings.Current.Format(
                "Core.Diagnostics.RunEvidence.Summary.Span",
                from.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                to.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
        }

        if (crashArtifacts == 0)
        {
            parts.Add(findings.Any(f => f.Kind == RunEvidenceKind.NoCrashReport)
                ? Strings.Current["Core.Diagnostics.RunEvidence.Summary.NoCrashReport.Explained"]
                : Strings.Current["Core.Diagnostics.RunEvidence.Summary.NoCrashReport.Unexplained"]);
        }

        var notable = findings.Count(f =>
            f.Kind is RunEvidenceKind.VersionFallback or RunEvidenceKind.UnhandledException);

        if (notable > 0)
            parts.Add(Strings.Current.Plural("Core.Diagnostics.RunEvidence.Summary.Notable", notable));

        return string.Join(" ", parts);
    }

    private sealed record LogRead(DateTimeOffset? First, DateTimeOffset? Last, string LastLine);

    // startedUtc is what keeps a daily log honest. ButterLib's ModLogs files are one per day, so a
    // capture copies the whole file including every session before this one, and reading those lines as
    // this run's is how a report comes to describe a run that ended an hour ago.
    private static LogRead ReadOne(
        CapturedArtifact artifact,
        DateTimeOffset startedUtc,
        DateOnly localDate,
        GameVersionSupportReport? onDisk,
        List<RunEvidenceFinding> findings,
        List<string> unreadable)
    {
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        var lastLine = string.Empty;

        // The window that turns "no match" into a finding. A negation on its own is not evidence: the
        // line naming a versioned assembly a few lines later is what makes it checkable against disk.
        var pending = -1;
        var pendingLine = string.Empty;

        // A line before the first readable timestamp cannot be placed, so it counts as this run's: the
        // alternative is discarding the header of every log that stamps nothing until its second line.
        var inRun = true;

        try
        {
            using var reader = new StreamReader(artifact.CapturedPath);

            for (var number = 0; number < MaxLines && reader.ReadLine() is { } raw; number++)
            {
                var line = Shorten(raw);

                if (line.Trim().Length == 0)
                    continue;

                if (Timestamp(line, localDate) is { } stamp)
                {
                    inRun = stamp >= startedUtc;

                    if (inRun)
                    {
                        first ??= stamp;
                        last = stamp;
                    }
                }

                if (!inRun)
                    continue;

                lastLine = line;

                if (Unhandled.IsMatch(line))
                    findings.Add(Crashed(artifact, line, last));

                if (DumpNotWritten.IsMatch(line))
                    findings.Add(NoReport(artifact, line));

                if (NoMatch.IsMatch(line))
                {
                    pending = 0;
                    pendingLine = line;
                }
                else if (pending >= 0 && ++pending > FallbackWindow)
                {
                    pending = -1;
                }

                if (pending < 0 || !Chose.IsMatch(line))
                    continue;

                if (VersionedAssembly.Match(line) is not { Success: true } assembly)
                    continue;

                findings.Add(Fallback(artifact, pendingLine, line, assembly.Groups["file"].Value, onDisk));
                pending = -1;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            unreadable.Add($"{artifact.CapturedPath} could not be read: {ex.Message}");
        }

        return new LogRead(first, last, lastLine);
    }

    private static RunEvidenceFinding Crashed(CapturedArtifact artifact, string line, DateTimeOffset? at) => new(
        RunEvidenceKind.UnhandledException,
        at is { } when
            ? Strings.Current.Format(
                "Core.Diagnostics.RunEvidence.Crashed.Headline.WithTime",
                when.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            : Strings.Current["Core.Diagnostics.RunEvidence.Crashed.Headline.NoTime"],
        line,
        Strings.Current.Format("Core.Diagnostics.RunEvidence.Crashed.Why", artifact.OriginalPath),
        string.Empty,
        artifact.OriginalPath);

    private static RunEvidenceFinding NoReport(CapturedArtifact artifact, string line) => new(
        RunEvidenceKind.NoCrashReport,
        Strings.Current["Core.Diagnostics.RunEvidence.NoReport.Headline"],
        line,
        Strings.Current.Format("Core.Diagnostics.RunEvidence.NoReport.Why", artifact.OriginalPath),
        string.Empty,
        artifact.OriginalPath);

    private static RunEvidenceFinding Fallback(
        CapturedArtifact artifact,
        string noMatchLine,
        string loadedLine,
        string fileName,
        GameVersionSupportReport? onDisk)
    {
        // Read off the lines themselves rather than the top of the file. Taking the first writer named
        // anywhere in a shared sink attributes ButterLib's own line to whichever mod happened to log
        // first that session, which is a wrong name on the row that matters most.
        var who = Writer(noMatchLine) ?? Writer(loadedLine)
            ?? Path.GetFileNameWithoutExtension(artifact.OriginalPath);

        var split = GameVersionSupport.Split(fileName);
        var set = split is { } parts ? onDisk?.FindByStem(parts.Stem) : null;
        var loaded = split?.Version;

        string why;

        if (onDisk is null || onDisk.GameVersion.IsEmpty)
        {
            why = Strings.Current["Core.Diagnostics.RunEvidence.Fallback.Why.NoGameVersion"];
        }
        else if (set is null)
        {
            why = Strings.Current.Format("Core.Diagnostics.RunEvidence.Fallback.Why.NoSet", onDisk.GameVersion);
        }
        else if (set.Supports(onDisk.GameVersion))
        {
            why = Strings.Current.Format(
                "Core.Diagnostics.RunEvidence.Fallback.Why.NotAGap", onDisk.GameVersion, set.DisplayName, set.FolderPath);
        }
        else
        {
            why = Strings.Current.Plural(
                "Core.Diagnostics.RunEvidence.Fallback.Why.Confirmed",
                set.Versions.Count, onDisk.GameVersion, set.DisplayName, set.Newest, set.FolderPath);
        }

        return new RunEvidenceFinding(
            RunEvidenceKind.VersionFallback,
            loaded is { } version
                ? Strings.Current.Format("Core.Diagnostics.RunEvidence.Fallback.Headline.WithVersion", who, version)
                : Strings.Current.Format("Core.Diagnostics.RunEvidence.Fallback.Headline.NoVersion", who, fileName),
            $"{noMatchLine}{Environment.NewLine}{loadedLine}",
            why,
            set?.ModuleId.Value ?? string.Empty,
            artifact.OriginalPath,
            fileName);
    }

    private static string? Writer(string line) =>
        BracketedOwner.Match(line) is { Success: true } named ? named.Groups["owner"].Value : null;

    private static string Shorten(string line) =>
        line.Length <= MaxLineLength ? line : line[..MaxLineLength] + " ...";

    // Mod loggers stamp lines four different ways on this install alone, so the parse tries the shapes
    // that actually appear rather than one format. A line with no readable stamp is simply not a
    // timestamp, and contributes nothing rather than a guess.
    private static DateTimeOffset? Timestamp(string line, DateOnly localDate)
    {
        if (BracketedStamp.Match(line) is { Success: true } bracketed
            && Parse(bracketed.Groups["stamp"].Value, localDate) is { } inBrackets)
        {
            return inBrackets;
        }

        return BareTime.Match(line) is { Success: true } bare
            ? Parse(bare.Groups["stamp"].Value, localDate)
            : null;
    }

    private static DateTimeOffset? Parse(string text, DateOnly localDate)
    {
        var trimmed = text.Trim().Replace(',', '.');

        if (trimmed.Contains('-', StringComparison.Ordinal) || trimmed.Contains('/', StringComparison.Ordinal))
        {
            return DateTimeOffset.TryParse(
                trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var offset)
                ? offset
                : null;
        }

        if (!TimeOnly.TryParse(trimmed, CultureInfo.InvariantCulture, out var time))
            return null;

        var moment = localDate.ToDateTime(time);

        return new DateTimeOffset(moment, TimeZoneInfo.Local.GetUtcOffset(moment));
    }
}
