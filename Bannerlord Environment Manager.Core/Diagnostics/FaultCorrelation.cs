using System.Globalization;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public enum FaultEvidenceKind
{
    // A minidump Windows kept in %LOCALAPPDATA%\CrashDumps for the same process.
    CrashDump,

    // A file the engine wrote whose name carries the process id, which is how rgl_log_37280.txt and
    // rgl_log_errors_37280.txt are known to belong to this run rather than to the one before it.
    GameLog,

    // A run BEM was watching when the fault happened, and the folder it copied evidence into.
    Capture,

    // The dated folder of artifacts the game itself wrote about this crash, which carries the process
    // id in the name of the engine log inside it.
    GameCrashFolder
}

// One other view of the same event, and how BEM knows it is the same event. How is on the record
// because a join by process id is a fact and a join by time is a coincidence that usually holds, and
// the reader has to be able to tell those apart.
public sealed record FaultEvidence(FaultEvidenceKind Kind, string Path, string How);

public sealed record CorrelatedFault(
    WindowsFaultRecord Record,
    IReadOnlyList<FaultEvidence> Evidence,
    IReadOnlyList<string> Gaps)
{
    public bool HasEvidence => Evidence.Count > 0;

    public string Describe()
    {
        var lines = new List<string>();

        if (Evidence.Count == 0)
        {
            lines.Add(Strings.Current["Core.Diagnostics.FaultCorrelation.NoEvidence"]);
        }
        else
        {
            lines.Add(Strings.Current.Plural(
                "Core.Diagnostics.FaultCorrelation.OtherViews", Evidence.Count));

            lines.AddRange(Evidence.Select(found => $"  {found.Path} ({found.How})"));
        }

        lines.AddRange(Gaps);

        return string.Join(Environment.NewLine, lines);
    }
}

// Ties a fault record to the evidence BEM already reads. A fault record, a minidump in
// %LOCALAPPDATA%\CrashDumps, the engine's own rgl_log_errors file and a captured run are four views of
// one process ending, and the process id is written into three of the four: the event log carries it as
// a field, Windows puts it in the dump's file name, and the engine puts it in its log's file name.
//
// Everything joined by process id is stated as such. Everything joined only by time says so, because a
// second copy of the game running at the same moment would break that join and nothing here can tell.
public static class FaultCorrelation
{
    // How long after a capture starts a fault still counts as belonging to it when the capture never
    // recorded a finish. A capture with no finish time is one that was interrupted, and pretending its
    // window is open forever would attach every later crash to it.
    private static readonly TimeSpan UnfinishedCaptureWindow = TimeSpan.FromHours(2);

    public static CorrelatedFault Join(
        WindowsFaultRecord record,
        IReadOnlyList<CrashDumpReading> dumps,
        IReadOnlyList<DiscoveredLog> logs,
        IReadOnlyList<ArtifactCapture> captures,
        IReadOnlyList<GameCrashFolderReading>? crashFolders = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(dumps);
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentNullException.ThrowIfNull(captures);

        var evidence = new List<FaultEvidence>();
        var gaps = new List<string>();

        if (record.HasProcessId)
        {
            var pid = record.ProcessId;

            foreach (var folder in crashFolders ?? [])
            {
                if (folder.ProcessId != pid)
                    continue;

                evidence.Add(new FaultEvidence(
                    FaultEvidenceKind.GameCrashFolder,
                    folder.Path,
                    Strings.Current.Format(
                        "Core.Diagnostics.FaultCorrelation.How.GameCrashFolder",
                        pid.ToString(CultureInfo.InvariantCulture))));
            }

            foreach (var dump in dumps)
            {
                if (CrashDumps.ProcessIdOf(dump.Path) != pid)
                    continue;

                if (!string.Equals(dump.ProcessName, record.ProcessStem, StringComparison.OrdinalIgnoreCase))
                    continue;

                evidence.Add(new FaultEvidence(
                    FaultEvidenceKind.CrashDump,
                    dump.Path,
                    Strings.Current.Format(
                        "Core.Diagnostics.FaultCorrelation.How.CrashDump", pid.ToString(CultureInfo.InvariantCulture))));
            }

            foreach (var log in logs)
            {
                if (!NameCarriesProcessId(Path.GetFileNameWithoutExtension(log.Path), pid))
                    continue;

                evidence.Add(new FaultEvidence(
                    FaultEvidenceKind.GameLog,
                    log.Path,
                    Strings.Current.Format(
                        "Core.Diagnostics.FaultCorrelation.How.GameLog", pid.ToString(CultureInfo.InvariantCulture))));
            }

            if (!evidence.Any(found => found.Kind == FaultEvidenceKind.CrashDump))
            {
                gaps.Add(Strings.Current["Core.Diagnostics.FaultCorrelation.Gap.NoCrashDump"]);
            }

            // A crash folder holds the engine's own logs for this run, so "no log carries this process
            // id" would be a false statement whenever one was found. The two must not contradict.
            if (!evidence.Any(found => found.Kind is FaultEvidenceKind.GameLog or FaultEvidenceKind.GameCrashFolder))
            {
                gaps.Add(Strings.Current["Core.Diagnostics.FaultCorrelation.Gap.NoGameLog"]);
            }
        }
        else
        {
            gaps.Add(Strings.Current["Core.Diagnostics.FaultCorrelation.Gap.NoProcessId"]);
        }

        foreach (var capture in captures)
        {
            if (!Covers(capture, record.When))
                continue;

            evidence.Add(new FaultEvidence(
                FaultEvidenceKind.Capture,
                capture.FolderPath,
                Strings.Current["Core.Diagnostics.FaultCorrelation.How.Capture"]));
        }

        if (!evidence.Any(found => found.Kind == FaultEvidenceKind.Capture))
        {
            gaps.Add(Strings.Current["Core.Diagnostics.FaultCorrelation.Gap.NoCapture"]);
        }

        return new CorrelatedFault(record, evidence, gaps);
    }

    public static IReadOnlyList<CorrelatedFault> JoinAll(
        IReadOnlyList<WindowsFaultRecord> records,
        IReadOnlyList<CrashDumpReading> dumps,
        IReadOnlyList<DiscoveredLog> logs,
        IReadOnlyList<ArtifactCapture> captures,
        IReadOnlyList<GameCrashFolderReading>? crashFolders = null)
    {
        ArgumentNullException.ThrowIfNull(records);

        return [.. records.Select(record => Join(record, dumps, logs, captures, crashFolders))];
    }

    // rgl_log_37280 and rgl_log_errors_37280 both end in the process id, and the character in front of
    // it has to be a non-digit so that 137280 is not read as a match for 37280.
    public static bool NameCarriesProcessId(string stem, int processId)
    {
        if (processId <= 0 || string.IsNullOrEmpty(stem))
            return false;

        var text = processId.ToString(CultureInfo.InvariantCulture);

        if (!stem.EndsWith(text, StringComparison.Ordinal))
            return false;

        var before = stem.Length - text.Length - 1;

        return before < 0 || !char.IsDigit(stem[before]);
    }

    private static bool Covers(ArtifactCapture capture, DateTimeOffset when)
    {
        if (when < capture.StartedUtc)
            return false;

        var end = capture.FinishedUtc ?? capture.StartedUtc + UnfinishedCaptureWindow;

        return when <= end;
    }
}
