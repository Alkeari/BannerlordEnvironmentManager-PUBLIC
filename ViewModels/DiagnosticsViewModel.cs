using System.Collections.ObjectModel;
using System.Threading;
using Microsoft.VisualBasic.FileIO;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.DryRun;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Modules.KnownIssues;
using BannerlordEnvironmentManager.Core.Report;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels
{
    public sealed class CrashModuleRowViewModel(string heading, string evidence, string note)
    {
        public string Heading { get; } = heading;

        public string Evidence { get; } = evidence;

        public string Note { get; } = note;
    }

    // One Windows Error Reporting minidump, read rather than counted. The game's own crash reports are
    // deleted by its uploader within seconds; these are not, and BEM had never opened one.
    public sealed class CrashDumpRowViewModel(
        CrashDumpReading reading,
        bool isOwn,
        System.Windows.Input.ICommand removeCommand)
    {
        public System.Windows.Input.ICommand RemoveCommand { get; } = removeCommand;

        public string Path { get; } = reading.Path;

        public string Title { get; } = $"{reading.Name} ({reading.SizeBytes / 1048576} MB)"
            + (isOwn ? Strings.Current["Diagnostics.OwnSuffix"] : string.Empty);

        public string Written { get; } = reading.Written.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        public string Headline { get; } = reading.Headline();

        public string Attribution { get; } = reading.DescribeAttribution();

        // Kept whole, holes and all. A frame the dump does not carry is the reason an answer stops
        // short, and dropping it would make the stack look like it named somebody.
        public ObservableCollection<string> Frames { get; } =
        [
            .. reading.Stack.Select(f => f.ModulePath is { Length: > 0 } module
                ? $"{f.Describe()}  <-  {module}"
                : f.Describe())
        ];

        public string Text => string.Join("\n", new[] { Title, Written, Headline, Attribution, Path }
            .Concat(Frames));
    }

    // One thing Windows wrote down about one process ending, and every other view of that same event
    // BEM could line up with it. The process id is what makes those a fact rather than a coincidence,
    // and a join made on the clock alone says so on its own line.
    public sealed class WindowsFaultRecordRowViewModel(CorrelatedFault fault)
    {
        public string When { get; } = fault.Record.When24;

        public string Detail { get; } = Describe(fault.Record);

        public ObservableCollection<string> Evidence { get; } =
            [.. fault.Evidence.Select(found => $"{found.Path}   ({found.How})")];

        // What lines up with nothing, and why. A fault with no dump beside it is a real state and has
        // to read differently from no fault at all.
        public ObservableCollection<string> Gaps { get; } = [.. fault.Gaps];

        public string Text => string.Join(
            "\n",
            new[] { When, Detail }.Concat(Evidence).Concat(Gaps));

        private static string Describe(WindowsFaultRecord record)
        {
            var parts = new List<string>();

            if (record.HasProcessId)
                parts.Add($"process id {record.ProcessId}");

            if (record.ProcessStarted is { } started)
                parts.Add($"started {started.ToLocalTime():HH:mm:ss}");

            if (record.ProcessVersion.Length > 0)
                parts.Add($"version {record.ProcessVersion}");

            if (record.FaultingModulePath.Length > 0)
                parts.Add(record.FaultingModulePath);

            if (record.Note.Length > 0)
                parts.Add(record.Note);

            if (record.ManagedStack.Length > 0)
                parts.Add(record.ManagedStack);

            return string.Join("   ", parts);
        }
    }

    // The same crash, seen more than once. This grouping is the whole point: one code at one offset
    // repeating is one instruction failing repeatedly, which is what makes a crash findable. It still
    // names no mod, and every row says so.
    public sealed class WindowsFaultRowViewModel(
        WindowsFaultGroup group,
        IReadOnlyList<CorrelatedFault> faults,
        bool isOwn,
        System.Windows.Input.ICommand copyCommand)
    {
        public System.Windows.Input.ICommand CopyCommand { get; } = copyCommand;

        public string Title { get; } = group.Title + (isOwn ? Strings.Current["Diagnostics.OwnSuffix"] : string.Empty);

        public string Occurrences { get; } = group.Describe();

        // Split out so the code and the offset are their own selectable line: they exist to be pasted
        // into a search, and burying them inside a sentence makes that a retyping job.
        public string Code { get; } = group.Newest.Code is { } code ? code.Hex : string.Empty;

        public string Meaning { get; } = group.Newest.Code is { } code ? code.Meaning : string.Empty;

        public string Offset { get; } = group.Newest.FaultOffset.Length > 0
            ? $"{group.Newest.FaultingModule} + {group.Newest.FaultOffset}"
            : string.Empty;

        public string Source { get; } = $"{group.Newest.Provider}, event {group.Newest.EventId}";

        public ObservableCollection<WindowsFaultRecordRowViewModel> Records { get; } =
            [.. faults.Select(fault => new WindowsFaultRecordRowViewModel(fault))];

        public string Text => string.Join(
            "\n",
            new[] { Title, Code, Meaning, Offset, Occurrences, Source }
                .Where(line => line.Length > 0)
                .Concat(Records.Select(record => record.Text)));
    }

    // One crash folder the game wrote, read as one crash report. The game does not write a crash
    // report as a file: it writes a dated folder of artifacts, and BEM used to read that folder file by
    // file, discard every file that did not parse as a managed exception trace, and then say it had
    // found no crash report while a complete one sat on disk.
    public sealed class GameCrashFolderRowViewModel(
        GameCrashFolderReading reading,
        System.Windows.Input.ICommand copyCommand,
        System.Windows.Input.ICommand openCommand)
    {
        public System.Windows.Input.ICommand CopyCommand { get; } = copyCommand;

        public System.Windows.Input.ICommand OpenCommand { get; } = openCommand;

        public string Path { get; } = reading.Path;

        public string Title { get; } = $"{reading.Name}: {reading.Headline()}";

        public string When { get; } = reading.FaultAt is { } fault
            ? Strings.Current.Format(
                "Diagnostics.GameCrashFolder.When.Fault", $"{fault.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}", reading.ProcessId)
            : Strings.Current.Format("Diagnostics.GameCrashFolder.When.Written", $"{reading.Written.ToLocalTime():yyyy-MM-dd HH:mm:ss}");

        public string Code { get; } = reading.Code is { } code
            ? $"{code.Hex} at {reading.FaultAddress}"
            : string.Empty;

        public string Meaning { get; } = reading.Code?.Meaning ?? string.Empty;

        public string RunTime { get; } = reading.RunTime is { } ran
            ? Strings.Current.Plural(
                "Diagnostics.GameCrashFolder.RunTime", (long)Math.Round(ran.TotalSeconds), $"{ran.TotalSeconds:0.0}")
            : string.Empty;

        // The derivation that turns a folder of artifacts into an answer, and the one most able to
        // overclaim, so the sentence it produces says which half is a fact and which is only a
        // narrowing.
        public string Boundary { get; } = reading.Boundary?.Describe()
            ?? (reading.LaunchOrder.Count > 0
                ? Strings.Current["Diagnostics.GameCrashFolder.Boundary.NoAttribution"]
                : Strings.Current["Diagnostics.GameCrashFolder.Boundary.NoCommandLine"]);

        // Kept as one selectable block rather than 242 rows: these exist to be read and pasted, and a
        // list this long as separate rows buries everything under it.
        public string HadInitialized { get; } = reading.Boundary is { } boundary
            ? string.Join(", ", boundary.HadInitialized)
            : string.Empty;

        public string NotObserved { get; } = reading.Boundary is { } boundary
            ? string.Join(", ", boundary.NotObserved)
            : string.Empty;

        public string HadInitializedHeading { get; } = reading.Boundary is { } boundary
            ? Strings.Current.Plural("Diagnostics.GameCrashFolder.HadInitializedHeading", boundary.HadInitialized.Count)
            : string.Empty;

        public string NotObservedHeading { get; } = reading.Boundary is { } boundary
            ? Strings.Current.Plural("Diagnostics.GameCrashFolder.NotObservedHeading", boundary.NotObserved.Count)
            : string.Empty;

        // Native frames, holes and all. The game writes a binary and a byte offset, so a frame names a
        // binary and never a method, and a frame it could not resolve is why an answer stops short.
        public ObservableCollection<string> Stack { get; } =
            [.. reading.Stack.Select(frame => frame.Describe())];

        public string Artifacts { get; } = string.Join(
            ", ",
            reading.Files.Select(System.IO.Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));

        public string Text => string.Join(
            "\n",
            new[] { Title, Path, When, Code, Meaning, RunTime, Boundary }
                .Where(line => line.Length > 0)
                .Concat([HadInitializedHeading, HadInitialized, NotObservedHeading, NotObserved])
                .Concat(Stack)
                .Concat([Artifacts])
                .Where(line => line.Length > 0));
    }

    // The control that carries a row's remedy. A row with no mechanical remedy has none of these and
    // says in one sentence what the user should do instead.
    public sealed class KnownIssueActionViewModel(
        string label,
        string tooltip,
        System.Windows.Input.ICommand command,
        object? parameter = null)
    {
        public string Label { get; } = label;

        public string Tooltip { get; } = tooltip;

        public System.Windows.Input.ICommand Command { get; } = command;

        public object? Parameter { get; } = parameter;
    }

    // One reorder, named by the two modules it involves. Every load-order remedy on this tab is this
    // move, and it goes through the Play tab's own fix path so undo and the live write apply.
    public sealed record LoadOrderMove(string MoverId, string AnchorId);

    public class KnownIssueRowViewModel(
        string heading,
        string detail,
        string note,
        string moduleId = "",
        string moduleFolderPath = "",
        IReadOnlyList<KnownIssueActionViewModel>? actions = null)
    {
        private readonly string rawHeading = heading;
        private Action<KnownIssueRowViewModel>? acceptRisk;

        public string Heading => Accepted ? $"{rawHeading} (accepted)" : rawHeading;

        public string Detail { get; } = detail;

        public string Note { get; } = note;

        // Detail and Note render as one TextBlock rather than two: WinUI cannot carry a text
        // selection across separate sibling TextBlocks, so splitting them meant a drag-select could
        // never reach both. Heading stays on its own control since it carries a different color.
        public string DetailAndNote => Note.Length > 0 ? $"{Detail}\n{Note}" : Detail;

        public string ModuleId { get; } = moduleId;

        public string ModuleFolderPath { get; } = moduleFolderPath;

        public IReadOnlyList<KnownIssueActionViewModel> Actions { get; } = actions ?? [];

        public virtual string Text => string.Join("\n", new[] { Heading, Detail, Note }.Where(l => l.Length > 0));

        // Opt-in, set only for the families that are a standing risk assessment rather than a fact or
        // a piece of evidence: a crash frame or a captured run is true regardless of anyone's opinion
        // of it, and never gets an accept button. A row nobody enabled this on has Category null, and
        // CanAcceptRisk is false for the same reason a note with no Fix has no Fix button: the feature
        // does not apply here rather than being denied.
        public string? Category { get; private set; }

        public bool Accepted { get; private set; }

        public bool CanAcceptRisk => Category is not null && !Accepted;

        internal void EnableAccept(string category, bool accepted, Action<KnownIssueRowViewModel> accept)
        {
            Category = category;
            Accepted = accepted;
            acceptRisk = accept;
        }

        internal void MarkAccepted() => Accepted = true;

        public void AcceptRisk() => acceptRisk?.Invoke(this);
    }

    // One captured run and what its own files say about it. Runs are shown together rather than one at
    // a time because the run BEM last started is often not the run the user is asking about: a boot
    // check started after a crash is its own run, and answering with only that one is how a session
    // that crashed came to be reported as "nothing was measured".
    public sealed class RunEvidenceRowViewModel(
        RunEvidenceReport report,
        IReadOnlyList<KnownIssueRowViewModel> findings,
        IReadOnlyList<KnownIssueActionViewModel> actions)
    {
        public string Title { get; } = report.Title;

        public string Summary { get; } = report.Summary;

        public string FolderPath { get; } = report.FolderPath;

        // Measured from the log lines rather than from a process handle BEM held, which is the only
        // figure here that survives a launcher handing the game on to another process.
        public string Timing { get; } = report.ObservedSpan is { } span
            ? Strings.Current.Format(
                "Diagnostics.RunEvidence.Timing", $"{span:hh\\:mm\\:ss}", $"{report.LastEntry?.ToLocalTime():HH:mm:ss}")
            : Strings.Current["Diagnostics.RunEvidence.NoTimestamp"];

        public string Unreadable { get; } = report.Unreadable.Count == 0
            ? string.Empty
            : Strings.Current.Plural(
                "Diagnostics.RunEvidence.Unreadable", report.Unreadable.Count, string.Join("; ", report.Unreadable));

        // Summary, Timing, FolderPath and Unreadable all render the same steel-gray body text below
        // Title, so they render as one TextBlock rather than four: WinUI cannot carry a text selection
        // across separate sibling TextBlocks, so splitting them meant a drag-select could never reach
        // more than one at a time.
        public string Body => string.Join("\n", new[] { Summary, Timing, FolderPath, Unreadable }.Where(l => l.Length > 0));

        public IReadOnlyList<KnownIssueRowViewModel> Findings { get; } = findings;

        public IReadOnlyList<KnownIssueActionViewModel> Actions { get; } = actions;
    }

    // One side of a rig conflict, offered as a choice rather than applied. Both mods work and both are
    // wanted by somebody; which one goes is not a decision BEM has any standing to make.
    public sealed class RigChoiceViewModel(
        string moduleId,
        string displayName,
        string evidence,
        bool appliedLast,
        System.Windows.Input.ICommand command)
    {
        public System.Windows.Input.ICommand Command { get; } = command;

        public string ModuleId { get; } = moduleId;

        public string Label { get; } = Strings.Current.Format("Diagnostics.RigChoice.DisableLabel", displayName);

        public string Evidence { get; } = evidence;

        public string AppliedLastNote { get; } = appliedLast
            ? Strings.Current["Diagnostics.RigChoice.AppliedLastNote"]
            : string.Empty;

        public bool IsAppliedLast { get; } = appliedLast;
    }

    // One contested attribute, shaped to be compared rather than read. The three facts that decide
    // whether a row matters sit in the header; the chain, the individual contested values, the ids and
    // the remedy sit behind the disclosure. The paragraph that used to repeat on every row is said once
    // for the section instead.
    public sealed class XmlOverlapRowViewModel(
        string headline,
        string outcome,
        string grade,
        string contestedPath,
        string chain,
        string contestsCaption,
        IReadOnlyList<string> contestLines,
        string idsCaption,
        string ids,
        IReadOnlyList<KnownIssueActionViewModel> actions,
        string moduleId,
        string moduleFolderPath)
        : KnownIssueRowViewModel(headline, outcome, chain, moduleId, moduleFolderPath, actions)
    {
        public string Headline { get; } = headline;

        public string Outcome { get; } = outcome;

        public string Grade { get; } = grade;

        public string ContestedPath { get; } = contestedPath;

        public string Chain { get; } = chain;

        public string ContestsCaption { get; } = contestsCaption;

        public IReadOnlyList<string> ContestLines { get; } = contestLines;

        // An expander is named by its AutomationProperties.Name alone: the text blocks inside a header
        // do not become the name of the toggle button that opens it. Without this a screen reader meets
        // hundreds of rows with nothing to tell them apart.
        public string AutomationName { get; } =
            string.Join(". ", new[] { headline, outcome, grade }.Where(line => line.Length > 0));

        public string IdsCaption { get; } = idsCaption;

        public string Ids { get; } = ids;

        // One line per value the game throws away, and a row can carry hundreds. They are put here the
        // first time somebody opens the row, so a page of closed rows costs nothing to lay out.
        public ObservableCollection<string> Contests { get; } = [];

        public void RealizeContests()
        {
            if (Contests.Count == ContestLines.Count)
                return;

            foreach (var line in ContestLines)
                Contests.Add(line);
        }

        public override string Text =>
            string.Join("\n", new[] { Headline, Outcome, Grade, ContestedPath, Chain, ContestsCaption }
                .Concat(ContestLines)
                .Concat([IdsCaption, Ids])
                .Where(l => l.Length > 0));
    }

    public sealed class RigConflictRowViewModel(
        string heading,
        string detail,
        string note,
        IReadOnlyList<RigChoiceViewModel> choices,
        string moduleId,
        string moduleFolderPath)
        : KnownIssueRowViewModel(heading, detail, note, moduleId, moduleFolderPath)
    {
        public IReadOnlyList<RigChoiceViewModel> Choices { get; } = choices;
    }

    // One member of a candidate set. There is no rank on this row and no ordering claim in its text:
    // the position is here so a name can be found in a load order of 242, and it says nothing about
    // how likely that module is.
    public sealed class CandidateRowViewModel(CandidateModule candidate)
    {
        public string ModuleId { get; } = candidate.ModuleId.Value;

        public string Position { get; } = $"#{candidate.Position}";

        public string Corroboration { get; } = candidate.Corroboration;

        public string Text => candidate.Describe();
    }

#if DEV_BEM
    // A row in the frame dropdown, which only the outer ring builds, so the type is only there too.
    public sealed class CrashFrameRowViewModel(ResolvedFrame resolved)
    {
        public CrashFrame Frame { get; } = resolved.Frame;

        public string Label { get; } = $"{resolved.Frame.Depth}. {resolved.Frame.QualifiedName}";

        public string Origin { get; } = resolved.Origin switch
        {
            FrameOrigin.Module => $"{resolved.ModuleId}",
            FrameOrigin.Game => Strings.Current["Diagnostics.CrashFrame.Origin.Game"],
            FrameOrigin.Bcl => Strings.Current["Diagnostics.CrashFrame.Origin.Framework"],
            _ => Strings.Current["Diagnostics.CrashFrame.Origin.Unknown"]
        };

        // Nothing here decides how the row is drawn or announced. The ring builds each dropdown row as
        // a ComboBoxItem out of Label and Origin, and puts Label on the item as its automation name so
        // a screen reader reads the frame rather than a type name.
    }
#endif

    public sealed class CrashReportRowViewModel(DiscoveredCrashReport report, CrashAttribution attribution)
    {
        public DiscoveredCrashReport Report { get; } = report;

        public CrashAttribution Attribution { get; } = attribution;

        public string Title { get; } = Strings.Current.Format(
            "Diagnostics.CrashReport.Title", report.Name, DiagnosticsViewModel.Describe(report.Source));

        public string Written { get; } = report.Written.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        public string Verdict { get; } = attribution.Verdict?.Headline ?? string.Empty;

        public string Occurrences { get; } = report.Occurrences > 1
            ? Strings.Current.Plural("Diagnostics.CrashReport.Occurrences", report.Occurrences)
            : string.Empty;

        public override string ToString() => Title;
    }

    public sealed class LogFindingRowViewModel(LogFinding finding)
    {
        public string Heading { get; } = finding.MatchCount > 1
            ? Strings.Current.Plural(
                "Diagnostics.LogFinding.Heading.MatchCount",
                finding.MatchCount,
                finding.LineNumber,
                finding.Signal.ToString().ToUpperInvariant())
            : Strings.Current.Format(
                "Diagnostics.LogFinding.Heading", finding.LineNumber, finding.Signal.ToString().ToUpperInvariant());

        public string Headline { get; } = finding.Headline;

        public string Excerpt { get; } = string.Join("\n", finding.Excerpt);

        // Headline and Excerpt render as one TextBlock rather than two, for the same reason as
        // KnownIssueRowViewModel's DetailAndNote: WinUI cannot carry a text selection across separate
        // sibling TextBlocks. Heading stays on its own control since it carries a different color.
        public string HeadlineAndExcerpt => Excerpt.Length > 0 ? $"{Headline}\n{Excerpt}" : Headline;
    }

    public sealed class ClearedBatchRowViewModel(ClearedBatchSummary batch)
    {
        public ClearedBatchSummary Batch { get; } = batch;

        public string Title { get; } = batch.Describe();

        public string Origins { get; } = batch.DescribeOrigins();

        public string BatchPath { get; } = batch.BatchPath;

        public override string ToString() => Title;
    }

    public sealed class ClearedFileRowViewModel(ClearedFile file)
    {
        public ClearedFile File { get; } = file;

        public string Title { get; } = Path.GetFileName(file.OriginPath);

        public string Origin { get; } =
            $"{file.OriginPath} - {DiagnosticsViewModel.DescribeSize(file.SizeBytes)}";

        public override string ToString() => Title;
    }

    // One module a search could turn off, carrying the two things the user sets on it before the
    // search starts: whether it is in the set being searched at all, and whether it was on for a run
    // they have already made and are about to record.
    public sealed partial class BisectionScopeRowViewModel : ObservableObject
    {
        public BisectionScopeRowViewModel(ModuleEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            Entry = entry;
            Title = entry.DisplayName;
            Detail = entry.Id.Value;
        }

        public ModuleEntry Entry { get; }

        public ModuleId Id => Entry.Id;

        public string Title { get; }

        public string Detail { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanBeInAnObservation))]
        public partial bool IsInScope { get; set; }

        // Only ever meaningful inside the scope. Everything outside it is on in every run by
        // definition, so there is nothing about it for the user to record.
        [ObservableProperty]
        public partial bool WasOn { get; set; }

        public bool CanBeInAnObservation => IsInScope;

        public override string ToString() => Title;
    }

    public sealed class BisectionObservationRowViewModel(BisectionObservation observation)
    {
        public BisectionObservation Observation { get; } = observation;

        public string Title { get; } = observation.Describe();

        public override string ToString() => Title;
    }

    // One mod a bug report could be drafted about, carrying why it is on the list. A row that says
    // "installed here, with no evidence pointing at it yet" and one that says "named by the search"
    // must never look the same in the picker.
    public sealed class BugReportSubjectViewModel(ModuleId id, string title, string why)
    {
        public ModuleId Id { get; } = id;

        public string Title { get; } = title;

        public string Why { get; } = why;

        public override string ToString() => $"{Title} - {Why}";
    }

    public sealed class BisectionSessionRowViewModel(BisectionSnapshot snapshot)
    {
        public BisectionSnapshot Snapshot { get; } = snapshot;

        public string Title { get; } = Strings.Current.Plural(
            "Diagnostics.BisectionSession.Title", snapshot.Settled, snapshot.Id, snapshot.Phase);

        public string Detail { get; } =
            Strings.Current.Format(
                "Diagnostics.BisectionSession.LastTouched", $"{snapshot.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}")
            + " " + Strings.Current.Plural("Diagnostics.BisectionSession.StillUnderTest", snapshot.Failing.Count);

        public override string ToString() => Title;
    }

    public sealed class LogRowViewModel(DiscoveredLog log, LogScan scan)
    {
        public DiscoveredLog Log { get; } = log;

        public LogScan Scan { get; } = scan;

        public string Title { get; } = log.Name;

        public string Owner { get; } = log.OwnerIsCertain
            ? Strings.Current.Format("Diagnostics.LogRow.Owner.Known", log.Owner, DiagnosticsViewModel.Describe(log.Source))
            : Strings.Current.Format("Diagnostics.LogRow.Owner.Unknown", DiagnosticsViewModel.Describe(log.Source));

        public string Written { get; } =
            $"{log.Written.ToLocalTime():yyyy-MM-dd HH:mm}, {DiagnosticsViewModel.DescribeSize(log.SizeBytes)}";

        public string Findings { get; } = Summarize(scan);

        public override string ToString() => Title;

        private static string Summarize(LogScan scan)
        {
            if (scan.Error is not null)
                return scan.Error;

            if (scan.MatchingLines == 0)
                return Strings.Current["Diagnostics.LogRow.NothingFound"];

            var passages = scan.Findings.Count == LogScanner.DefaultMaxFindings
                ? Strings.Current.Plural("Diagnostics.LogRow.Passages.Capped", scan.Findings.Count)
                : Strings.Current.Plural("Diagnostics.LogRow.Passages.Count", scan.Findings.Count);

            return Strings.Current.Plural("Diagnostics.LogRow.Summary", scan.MatchingLines, passages);
        }
    }

    public partial class DiagnosticsViewModel : BaseViewModel
    {
        private readonly Lock indexLock = new();
        private AssemblyIndex index = AssemblyIndex.Empty;
        private string indexedInstallPath = string.Empty;

        // Clearing works from what the locators actually found on the last scan, never from a folder
        // typed in anywhere, so nothing outside the searched log and crash folders can be moved.
        private IReadOnlyList<ClearCandidate> logCandidates = [];
        private IReadOnlyList<string> logRoots = [];
        private IReadOnlyList<ClearCandidate> reportCandidates = [];
        private IReadOnlyList<string> reportRoots = [];
        private IReadOnlyList<ClearCandidate> captureCandidates = [];

        // What the last scan found on disk, kept so a Windows fault record can be lined up against it
        // without walking every folder a second time.
        private IReadOnlyList<DiscoveredLog> lastLogs = [];
        private IReadOnlyList<CrashDumpReading> lastDumps = [];

        // Logs holds what the filter lets through; this holds every log the scan found, so turning the
        // filter off costs nothing and the counts can name what is hidden.
        private readonly List<LogRowViewModel> allLogs = [];

        // Constructed here rather than on the page, because reading it is what puts the user's limits in
        // front of the background prune, and a capture can start before any diagnostics page is opened.
        private readonly CaptureRetentionSettings retentionSettings = new();

        // Writing the stored limits into the properties raises their changed handlers, and measuring the
        // capture store from there would walk it on every start whether or not the page is ever opened.
        private bool readingRetention;

        // Findings the user has already read and chosen to stop counting against the Health badge:
        // an install check, a boot check result. Mod Safety keeps its own acceptance on ModSafetyViewModel,
        // since its findings are graded and a known-bad verdict is never acceptable the same way a rig
        // conflict is.
        // Built on each read rather than held: an accepted finding belongs to the version it was
        // accepted on, and the version dropdown can change between two reads of this. A held store kept
        // showing the Steam install's accepted risks against a version that has none of those mods.
        private static AcceptedFindingStore AcceptedFindings =>
            new(AcceptedFindingStore.DefaultPath(ResolveDataRoot()));

        public DiagnosticsViewModel()
        {
            Title = "Diagnostics";
            StatusMessage = Strings.Current["Diagnostics.InitialStatus"];

            ReadRetention();
        }

        // Everything on this page describes one version's install: its crash reports, its logs, its
        // assemblies, its XML overlaps, its boot checks. A version switch drops all of it rather than
        // showing it under the new version's name. The log and crash sweep is not re-run here, for the
        // reason ShellViewModels gives: it walks the whole install, and an empty list saying so beats a
        // wrong one.
        public void ClearForVersionChange()
        {
            Reports.Clear();
            Logs.Clear();
            LogFindings.Clear();
            Suspects.Clear();
            NotSuspected.Clear();
            SelectedReport = null;

            RigIssues.Clear();
            RigConflictRows.Clear();
            AssemblyIssues.Clear();
            HarmonyPatchIssues.Clear();
            ShadowedModules.Clear();
            DuplicateIds.Clear();
            GameAssemblyCopies.Clear();
            RecountInstallChecks();

            XmlOverlapConflicts.Clear();
            XmlOverlapOverrides.Clear();
            Collisions.Clear();

            DryRunFindings.Clear();
            AcceptedBootCheckCount = 0;
        }

        public ObservableCollection<CrashReportRowViewModel> Reports { get; } = [];

        public ObservableCollection<LogRowViewModel> Logs { get; } = [];

        public ObservableCollection<LogFindingRowViewModel> LogFindings { get; } = [];

        public ObservableCollection<CrashModuleRowViewModel> Suspects { get; } = [];

        public ObservableCollection<CrashModuleRowViewModel> NotSuspected { get; } = [];

        public ObservableCollection<string> Cleared { get; } = [];

        // Why the headline says what it says, and on a refusal, what stopped an answer. The engine
        // computes these and nothing showed them, so an honest "I do not know" arrived on screen with
        // no reasoning behind it and read as the feature failing rather than the feature working.
        public ObservableCollection<string> VerdictDetail { get; } = [];

        public ObservableCollection<string> Unknowns { get; } = [];

        // The set the crash narrows to, and the arithmetic that produced it. Deliberately not merged
        // into Suspects: a suspect is ranked and a set member is not, and putting the two in one list
        // would let a member of the set be read as the strongest name in the report.
        public ObservableCollection<CandidateRowViewModel> Candidates { get; } = [];

        public ObservableCollection<string> NarrowingSteps { get; } = [];

        public ObservableCollection<string> NarrowingRuledOut { get; } = [];

        public ObservableCollection<string> NarrowingCaveats { get; } = [];

        public ObservableCollection<ClearedBatchRowViewModel> ClearedBatches { get; } = [];

        public ObservableCollection<ClearedFileRowViewModel> ClearedBatchFiles { get; } = [];

        public ObservableCollection<RigConflictRowViewModel> RigConflictRows { get; } = [];

        public ObservableCollection<KnownIssueRowViewModel> RigIssues { get; } = [];

#if DEV_BEM
        // The outer ring: the frame list exists to say which frame the decompiler should read, and
        // nothing outside the ring decompiles. The attribution these rows are built from is read and
        // ranked in every build; only choosing one to open is the author's move.
        public ObservableCollection<CrashFrameRowViewModel> Frames { get; } = [];
#endif

        // The crash evidence that was on disk the whole time. Windows keeps a minidump of a process
        // that faults in %LOCALAPPDATA%\CrashDumps and nothing races BEM to delete it.
        public ObservableCollection<CrashDumpRowViewModel> CrashDumpRows { get; } = [];

        // What Windows itself recorded about the game ending. It is the one source that survives a
        // sweep of every crash folder on the machine, and the only one that saw a run which wrote no
        // log and left no managed exception in its dump.
        public ObservableCollection<WindowsFaultRowViewModel> WindowsFaultRows { get; } = [];

        // The crash folders the game wrote. A crash report from the game is a dated folder of
        // artifacts, not a file, and reading it file by file found nothing in a complete crash.
        public ObservableCollection<GameCrashFolderRowViewModel> GameCrashFolderRows { get; } = [];

        public ObservableCollection<KnownIssueRowViewModel> AssemblyIssues { get; } = [];

        // A Harmony patch naming a type or a method that is not on this install. Harmony throws the
        // moment it cannot resolve one, which takes the whole module's PatchAll down with it, and it
        // is all readable from metadata without launching anything.
        public ObservableCollection<KnownIssueRowViewModel> HarmonyPatchIssues { get; } = [];

        public ObservableCollection<KnownIssueRowViewModel> ShadowedModules { get; } = [];

        // Two folders under Modules declaring one module id, which is a different finding from
        // ShadowedModules above: the game starts with a Workshop copy sitting inert, and refuses to
        // load at all with this pair, naming neither folder when it does.
        public ObservableCollection<KnownIssueRowViewModel> DuplicateIds { get; } = [];

        public ObservableCollection<KnownIssueRowViewModel> GraphicsAdapters { get; } = [];

        // A module shipping per-game-version assemblies where none is built for the game that is
        // installed. This needs no launch at all, which is why it sits with the other install checks.
        public ObservableCollection<KnownIssueRowViewModel> VersionGaps { get; } = [];

        // What BEM's own capture store already holds about the last few runs, read rather than left
        // sitting there. A run that captured twelve logs must never be reported as nothing measured.
        public ObservableCollection<RunEvidenceRowViewModel> RunEvidenceRows { get; } = [];

        // A merge the game cannot finish, which is a different thing from a value it loses. These come
        // first everywhere they appear, because none of the rest matters on an install that crashes.
        public ObservableCollection<KnownIssueRowViewModel> MergeFaults { get; } = [];

        public ObservableCollection<XmlOverlapRowViewModel> XmlOverlapConflicts { get; } = [];

        public ObservableCollection<XmlOverlapRowViewModel> XmlOverlapOverrides { get; } = [];

        public ObservableCollection<KnownIssueRowViewModel> XmlDatasetProblems { get; } = [];

        // A registration the merge pipeline never had any business finding, because the module ships the
        // file where the interface or the localization system reads it instead. Listed apart from the
        // problems, because calling these a failure to read was a false statement about the game.
        public ObservableCollection<KnownIssueRowViewModel> XmlDatasetsLoadedElsewhere { get; } = [];

        // BEM runs the stylesheets a mod registers instead of admitting it cannot, so what each one did
        // is a counted result rather than a gap. A transform BEM could not run is listed here too.
        public ObservableCollection<KnownIssueRowViewModel> XmlTransforms { get; } = [];

        // A mod carrying the game's own assemblies. One row per module, not per file: a mod packaged
        // with its whole build output brings dozens and they all say the same thing.
        public ObservableCollection<KnownIssueRowViewModel> GameAssemblyCopies { get; } = [];

        [ObservableProperty]
        public partial string StatusMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string GameInstallPath { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ScanLogsAndCrashReportsCommand))]
        [NotifyCanExecuteChangedFor(nameof(ClearLogsCommand))]
        [NotifyCanExecuteChangedFor(nameof(ClearCrashReportsCommand))]
        [NotifyCanExecuteChangedFor(nameof(ClearLogsAndCrashReportsCommand))]
        [NotifyCanExecuteChangedFor(nameof(ListClearedBatchesCommand))]
        [NotifyCanExecuteChangedFor(nameof(ClearCapturesCommand))]
        [NotifyCanExecuteChangedFor(nameof(PruneCapturesCommand))]
        [NotifyCanExecuteChangedFor(nameof(RestoreClearedBatchCommand))]
        [NotifyCanExecuteChangedFor(nameof(RestoreClearedFileCommand))]
        [NotifyCanExecuteChangedFor(nameof(DiscardClearedBatchCommand))]
        public partial bool IsBusy { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CheckInstallCommand))]
        public partial bool IsCheckingInstall { get; set; }

        [ObservableProperty]
        public partial string KnownIssuesStatus { get; set; } = string.Empty;

        // Whether the verdict sections have anything to be about. Without it the seven headings under
        // the headline draw against no report at all, which reads as a page that found nothing rather
        // than one that has not been asked yet.
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartGuidedBisectionCommand))]
        [NotifyPropertyChangedFor(nameof(HasSelectedReport))]
        [NotifyPropertyChangedFor(nameof(NoReportSelectedText))]
        public partial CrashReportRowViewModel? SelectedReport { get; set; }

        public bool HasSelectedReport => SelectedReport is not null;

        public string NoReportSelectedText =>
            SelectedReport is not null
                ? string.Empty
                : Reports.Count > 0
                    ? Strings.Current["Diagnostics.NoReportSelected.HasReports"]
                    : Strings.Current["Diagnostics.NoReportSelected.NoReports"];

        [ObservableProperty]
        public partial LogRowViewModel? SelectedLog { get; set; }

        [ObservableProperty]
        public partial string LogsStatus { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string LogFolders { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool ShowOnlyLogsWithFindings { get; set; } = true;

        [ObservableProperty]
        public partial string LogFilterSummary { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string LogFindingsEmptyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Headline { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string WhatThrew { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NextStep { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string SuspectsEmptyText { get; set; } = string.Empty;

        // The set the crash narrows to. HasNarrowing decides whether the section exists at all: with
        // no enhanced frame, no call scan or no command line there is nothing to show, and an empty
        // panel taking space is worse than an absent one.
        [ObservableProperty]
        public partial bool HasNarrowing { get; set; }

        [ObservableProperty]
        public partial string NarrowingHeadline { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NarrowingMechanism { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NarrowingBoundaryFact { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NarrowingNecessity { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NarrowingContainment { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NarrowingPromotion { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NarrowingDisproof { get; set; } = string.Empty;

        // The set as one selectable line of ids, which is the form it gets pasted into a bisection,
        // a forum post or a launcher in.
        [ObservableProperty]
        public partial string NarrowingSetIds { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NotSuspectedEmptyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ClearedEmptyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string UnknownsEmptyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string SearchedFolders { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RestoreClearedBatchCommand))]
        [NotifyCanExecuteChangedFor(nameof(DiscardClearedBatchCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyClearedBatchCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyClearedBatchPathCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyClearedBatchOriginsCommand))]
        public partial ClearedBatchRowViewModel? SelectedClearedBatch { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RestoreClearedFileCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyClearedFileCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyClearedFileOriginCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyClearedFileStoredPathCommand))]
        public partial ClearedFileRowViewModel? SelectedClearedFile { get; set; }

        [ObservableProperty]
        public partial string ClearedStoreStatus { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string RigIssuesEmptyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string RigOrderStatusText { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ApplyRigOrderFixCommand))]
        public partial bool CanApplyRigOrderFix { get; set; }

#if DEV_BEM
        // The outer ring, whole. Reading a compiled body back is the author's move, so the frame list,
        // what came out of the decompiler and the state of the run are all here rather than half here:
        // a screen with nothing that can fill it is not a feature, and the ranked suspects, the frame
        // attribution and the crash reading are what a player is served by in every build.
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DecompileSelectedFrameCommand))]
        public partial CrashFrameRowViewModel? SelectedFrame { get; set; }

        [ObservableProperty]
        public partial string DecompiledSource { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string DecompileStatus { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ShowFaultingMethodCommand))]
        [NotifyCanExecuteChangedFor(nameof(DecompileSelectedFrameCommand))]
        [NotifyCanExecuteChangedFor(nameof(CancelDecompileCommand))]
        public partial bool IsDecompiling { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(OpenInDotPeekCommand))]
        public partial string DecompiledAssemblyPath { get; set; } = string.Empty;

        // Handing the assembly to a second decompiler is the same move one step further out.
        //
        // Absent means absent: with no dotPeek on this machine the button is not shown, and BEM never
        // offers to fetch one.
        public string DotPeekPath { get; } = DotPeekLocator.Locate() ?? string.Empty;

        public bool HasDotPeek => DotPeekPath.Length > 0;
#endif

        [ObservableProperty]
        public partial string AssemblyIssuesEmptyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string HarmonyPatchIssuesEmptyText { get; set; } = string.Empty;

        // What the sweep could not answer, said out loud. A class that picks its own targets while the
        // game runs has no static answer, and counting those as clean would let this check claim more
        // than it looked at.
        [ObservableProperty]
        public partial string HarmonyPatchesUncheckedText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string CrashDumpsStatus { get; set; } = string.Empty;

        // Said by the reading itself, because it is the only thing that knows which of three different
        // statements applies: BEM could not read the log, it read it and Windows recorded nothing, or
        // it read it and the log no longer reaches back as far as the crash being asked about.
        [ObservableProperty]
        public partial string WindowsFaultsStatus { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string GameCrashFoldersStatus { get; set; } = string.Empty;

        // Which providers and event ids BEM reads, listed rather than buried in a query, so what this
        // section did and did not look at is inspectable.
        public string WindowsFaultSourcesText { get; } = Strings.Current.Format(
            "Diagnostics.WindowsFaultSourcesText", WindowsFaultLog.Describe(WindowsFaultLog.Sources));

        // What Windows is set to keep, and what that costs. A mini dump leaves out the compiled code,
        // so a frame belonging to a mod's own method has nothing behind it to resolve and the stack
        // comes back with holes in it. Saying so is the difference between an answer that stops short
        // and an answer that looks like BEM failed.
        [ObservableProperty]
        public partial string DumpSettingsText { get; set; } = string.Empty;

        [ObservableProperty]
#if DEV_BEM
        [NotifyCanExecuteChangedFor(nameof(RestoreDumpSettingsCommand))]
#endif
        public partial bool CanRestoreDumpSettings { get; set; }

        [ObservableProperty]
        public partial string ShadowedModulesEmptyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string DuplicateIdsEmptyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string GraphicsAdaptersEmptyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string VersionGapsEmptyText { get; set; } = string.Empty;

        // How the rows below it are graded, said once for the section instead of repeated on each row.
        [ObservableProperty]
        public partial string VersionGapsGradingText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string RunEvidenceSummary { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ReadRunEvidenceCommand))]
        public partial bool IsReadingRunEvidence { get; set; }

        [ObservableProperty]
        public partial string MergeFaultsSummary { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string MergeFaultsEmptyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string XmlOverlapSummary { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string XmlOverlapConflictsEmptyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string XmlOverlapOverridesEmptyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string XmlDatasetProblemsEmptyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string XmlDatasetsLoadedElsewhereText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string XmlTransformsEmptyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string GameAssemblyCopiesEmptyText { get; set; } = string.Empty;

        // What the Install Checks navigation item shows a count of. Only the rows that mean something is
        // wrong: the rig conflicts, the assemblies two mods disagree on, the modules installed twice, the
        // patches with no target and the mods shipping rebuilt copies of the game's own files. The
        // stylesheet and dataset panels are deliberately out, because they are a description of what the
        // merge does rather than a list of faults, and a count nobody can act on is not worth a badge.
        [ObservableProperty]
        public partial int InstallCheckFindingCount { get; set; }

        // Never folded into InstallCheckFindingCount going quiet: an accepted finding is read, not
        // erased, and the badge saying only "0" would read as a clean install that never had anything
        // to accept.
        [ObservableProperty]
        public partial int AcceptedInstallCheckCount { get; set; }

        // Whether a section has anything in it, as an observable fact rather than a collection count.
        // Advanced Mode hides a section only when it is empty, and a binding that failed to notice a
        // scan filling one would hide a live finding, which is the one thing that rule forbids.
        [ObservableProperty]
        public partial bool HasXmlTransforms { get; set; }

        [ObservableProperty]
        public partial bool HasBaseGameOverrides { get; set; }

        [ObservableProperty]
        public partial bool HasFirstChance { get; set; }

        // Overlaps that compose losslessly. They are the large majority and they are not findings, so
        // the count is stated as a result of the check and never mixed into a problem list.
        [ObservableProperty]
        public partial string XmlComposedText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string XmlTransformsUncertainText { get; set; } = string.Empty;

        // The evidence every registry-derived band rests on. A stale or missing registry does not stop
        // an attribution, it weakens it, so what the report was analyzed against is stated rather than
        // left for the reader to assume.
        [ObservableProperty]
        public partial string PatchRegistryStatusText { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RefreshPatchRegistryCommand))]
        public partial bool PatchRegistryNeedsRefresh { get; set; }

        // The module names behind the sentence above. They belong on this page, but a heavily modded
        // install changes dozens of them at once and pasting those into the sentence turns a status
        // line into a paragraph. Folded away, and copyable in one action.
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CopyPatchRegistryDetailsCommand))]
        public partial string PatchRegistryDetailsText { get; set; } = string.Empty;

        // What the last capture attempt actually did. A dry run that started and produced nothing used
        // to leave this panel unchanged, which reads exactly like the button having done nothing.
        [ObservableProperty]
        public partial string PatchRegistryCaptureOutcomeText { get; set; } = string.Empty;

        private bool HasPatchRegistryDetails => !string.IsNullOrEmpty(PatchRegistryDetailsText);

        [RelayCommand(CanExecute = nameof(HasPatchRegistryDetails))]
        private void CopyPatchRegistryDetails() =>
            Copy(PatchRegistryDetailsText, Strings.Current["Diagnostics.Copy.What.ModuleNames"]);

        public ObservableCollection<KnownIssueRowViewModel> Collisions { get; } = [];

        [ObservableProperty]
        public partial string CollisionSummary { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string CollisionExclusions { get; set; } = string.Empty;

        private void ShowRegistry(PatchRegistryStatus status, PatchCollisionReport collisions)
        {
            PatchRegistryStatusText = status.Message;
            PatchRegistryNeedsRefresh = status.NeedsRefresh;
            PatchRegistryDetailsText = status.Details;

            CollisionSummary = collisions.Summary;
            CollisionExclusions = collisions.Exclusions.Count == 0
                ? string.Empty
                : Strings.Current["Diagnostics.Collisions.NotListed"] + " " + string.Join(" ", collisions.Exclusions.Select(
                    e => Strings.Current.Format("Diagnostics.Collisions.ExclusionReason", e.Methods, e.Rule, e.Why)));

            Collisions.Clear();

            foreach (var collision in collisions.Collisions)
            {
                Collisions.Add(new KnownIssueRowViewModel(
                    collision.IsContended
                        ? $"{collision.Target}, contended by {string.Join(", ", collision.Modules.Select(m => m.Value))}"
                        : $"{collision.Target}, stacked by {string.Join(", ", collision.Modules.Select(m => m.Value))}",
                    collision.Why,
                    string.Join("; ", collision.Patches.Select(
                        p => $"{p.ModuleId} {p.Kind.ToString().ToLowerInvariant()} at {p.PatchMethod}"))));
            }
        }

        // The offer belongs where the gap shows up. A crash analyzed against a stale or missing
        // registry says so on the report, and the fix is one button away rather than on another tab.
        [RelayCommand(CanExecute = nameof(PatchRegistryNeedsRefresh))]
        private async Task RefreshPatchRegistryAsync()
        {
            StatusMessage = Strings.Current["Diagnostics.Registry.StartingCapture"];

            PatchRegistryCaptureOutcomeText = string.Empty;
            lastRegistrySave = null;

            var startedAt = DateTimeOffset.Now;

            var verdict = await ShellViewModels.Instance.Environment.RunDryRunAsync();

            // "No dry run was started" and "a dry run started and captured nothing" are different
            // statements, and only the second one means the game died. They are never merged.
            if (verdict is null)
            {
                var why = ShellViewModels.Instance.Environment.StatusMessage;

                StatusMessage = Strings.Current.Format("Diagnostics.Registry.NoDryRunStarted", why);
                PatchRegistryCaptureOutcomeText =
                    Strings.Current.Format("Diagnostics.Registry.NoDryRunStarted.Unchanged", why);
                return;
            }

            await ScanLogsAndCrashReportsAsync();

            // Set after the scan on purpose: the scan rewrites the status line above it, and this is
            // the sentence about the attempt the user just made.
            if (lastRegistrySave is not { } saved)
            {
                PatchRegistryCaptureOutcomeText =
                    Strings.Current.Format("Diagnostics.Registry.NoResultReceived", verdict.Summary);
                return;
            }

            if (saved.Saved)
            {
                PatchRegistryCaptureOutcomeText =
                    Strings.Current.Format("Diagnostics.Registry.CapturedFresh", saved.Message);
                return;
            }

            PatchRegistryCaptureOutcomeText =
                Strings.Current.Format("Diagnostics.Registry.NoFreshRegistry", saved.Message)
                + await FaultsWhileRunningAsync(startedAt);
        }

        // A failed capture has evidence available rather than a shrug: Windows records a fault for the
        // game whether or not the game managed to write anything itself. Reading it here is what turns
        // "nothing happened" into the reason nothing happened.
        private static async Task<string> FaultsWhileRunningAsync(DateTimeOffset since)
        {
            try
            {
                var reading = await Task.Run(() => WindowsFaultLog.ReadForTheGame());

                if (!reading.CouldRead)
                    return string.Empty;

                var during = reading.Records.Where(record => record.When >= since).ToList();

                // "Found nothing" and "could not look" read as opposites, so the empty case says which
                // one it is rather than staying quiet.
                return during.Count == 0
                    ? " " + Strings.Current["Diagnostics.Registry.NoFaultWhileRunning"]
                    : " " + Strings.Current.Plural(
                        "Diagnostics.Registry.FaultWhileRunning", during.Count, during[0].Headline(), during[0].When24);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to read the Windows fault log after a registry capture");
                return string.Empty;
            }
        }

        private PatchRegistrySave? lastRegistrySave;

        // The dry run itself is backend. What it found is read here, beside the crash reports it
        // explains, and it never gets a tab of its own.
        public ObservableCollection<KnownIssueRowViewModel> DryRunFindings { get; } = [];

        // Never folded into the Health badge's boot count going quiet, same reason as install checks:
        // an accepted boot-check finding is read, not erased.
        [ObservableProperty]
        public partial int AcceptedBootCheckCount { get; set; }

        [ObservableProperty]
        public partial string DryRunSummary { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartAutomaticBisectionCommand))]
        public partial bool DryRunFailed { get; set; }

        private IReadOnlyList<ModuleEntry> lastDryRunLoadOrder = [];

        public void ShowDryRun(DryRunVerdict verdict, PatchRegistrySave saved, IReadOnlyList<ModuleEntry> loadOrder)
        {
            ArgumentNullException.ThrowIfNull(verdict);

            lastRegistrySave = saved;
            lastDryRunLoadOrder = loadOrder ?? [];
            DryRunFailed = verdict.Status is DryRunStatus.ModuleThrew or DryRunStatus.StoppedDuringLoad;

            var lines = new List<string> { verdict.Summary, saved.Message };

            if (verdict.SubModuleCount is { } count)
                lines.Add(Strings.Current.Plural("Diagnostics.DryRun.SubModulesSeen", count));

            // Restored, would not restore, and taken back out are three different things, and the
            // clean sentence is a claim about files BEM held a copy of. The wording lives in Core
            // beside the outcome it describes, so it is covered by the tests that produce it.
            lines.AddRange(verdict.ConfigOutcome?.Describe()
                ?? [verdict.ConfigChanges.Count == 0
                    ? Strings.Current["Diagnostics.DryRun.ConfigsNotCopied"]
                    : Strings.Current.Plural(
                        "Diagnostics.DryRun.ConfigsChanged", verdict.ConfigChanges.Count, string.Join("; ", verdict.ConfigChanges))]);

            // This used to read "Took 00:02", which was taken for how long the game ran and is not. It
            // measures the process BEM started, and a launcher that hands the game on to another
            // process exits at once, so the figure can be seconds while the session runs for minutes.
            // How long the run actually lasted is measured from the logs, under Runs BEM captured.
            lines.Add(Strings.Current.Format(
                "Diagnostics.DryRun.Elapsed", $"{verdict.Elapsed:mm\\:ss}", verdict.RunId));

            DryRunSummary = string.Join(" ", lines);

            DryRunFindings.Clear();

            var acceptedBootKeys = AcceptedFindings.LoadKeys();

            void EnableBootAccept(KnownIssueRowViewModel row) => row.EnableAccept(
                "BootCheck", acceptedBootKeys.Contains(AcceptedFindingStore.KeyFor("BootCheck", row.Heading)), AcceptFinding);

            foreach (var failure in verdict.FailedModules)
            {
                var failureRow = new KnownIssueRowViewModel(
                    Strings.Current.Format("Diagnostics.DryRun.ThrewWhileLoading", failure.Label),
                    failure.ExceptionType is { Length: > 0 } type
                        ? $"{type}: {failure.ExceptionMessage}"
                        : Strings.Current["Diagnostics.DryRun.NoExceptionCaptured"],
                    failure.ExceptionStack ?? string.Empty,
                    failure.Id);

                EnableBootAccept(failureRow);
                DryRunFindings.Add(failureRow);
            }

            if (verdict.ModuleLoadingWhenStopped is { Length: > 0 } stopped)
            {
                var stoppedRow = new KnownIssueRowViewModel(
                    Strings.Current.Format("Diagnostics.DryRun.WasLoadingWhenStopped", stopped),
                    Strings.Current["Diagnostics.DryRun.BreadcrumbsEnd"],
                    string.Empty,
                    stopped);

                EnableBootAccept(stoppedRow);
                DryRunFindings.Add(stoppedRow);
            }

            // Not offered here: the headline is the same generic sentence for every companion problem
            // regardless of what actually went wrong, so accepting one would silently cover every
            // future one that happens to share it.
            foreach (var error in verdict.Result?.CompanionErrors ?? [])
                DryRunFindings.Add(new KnownIssueRowViewModel(Strings.Current["Diagnostics.DryRun.CompanionProblem"], error, string.Empty));

            AcceptedBootCheckCount = DryRunFindings.Count(r => r.Accepted);

            // A verdict about the companion is not a verdict about the run. The capture store already
            // holds the logs, so it is read here rather than left as evidence BEM collected and ignored.
            _ = ReadRunEvidenceCommand.ExecuteAsync(null);
        }

        // How many runs are worth putting on screen at once. The one that crashed is rarely the last one
        // started, because the natural next thing to do after a crash is start a check, so a single run
        // is not enough; a page of them is not readable.
        private const int RecentRuns = 4;

        // Reads BEM's own capture store: what the last few runs wrote, when they stopped writing, and
        // anything in those files that names a version gap or a fault. This is the answer to a run being
        // reported as "nothing was measured" while twelve of its logs sat in the store.
        [RelayCommand(CanExecute = nameof(CanReadRunEvidence))]
        private async Task ReadRunEvidenceAsync()
        {
            if (IsReadingRunEvidence)
                return;

            IsReadingRunEvidence = true;

            try
            {
                var install = ResolveInstallPath();

                var reports = await Task.Run(() =>
                {
                    var version = GameVersionReader.Read(install);

                    var support = version.IsEmpty
                        ? null
                        : GameVersionSupport.Inspect(ReadModuleEntries(install), version);

                    return RunEvidence.ReadStore(CrashArtifactPaths.GetDefaultRoot(ResolveDataRoot()), RecentRuns, support);
                });

                ShowRunEvidence(reports);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Reading the captured runs failed");
                RunEvidenceSummary = Strings.Current.Format("Diagnostics.RunEvidence.CouldNotRead", ex.Message);
            }
            finally
            {
                IsReadingRunEvidence = false;
            }
        }

        private bool CanReadRunEvidence() => !IsReadingRunEvidence;

        private void ShowRunEvidence(IReadOnlyList<RunEvidenceReport> reports)
        {
            RunEvidenceRows.Clear();

            foreach (var report in reports)
            {
                var findings = report.Findings
                    .Select(finding => new KnownIssueRowViewModel(
                        finding.Headline, finding.Detail, finding.Why, finding.Owner, string.Empty))
                    .ToList();

                RunEvidenceRows.Add(new RunEvidenceRowViewModel(
                    report,
                    findings,
                    [
                        new KnownIssueActionViewModel(
                            Strings.Current["Diagnostics.RunEvidence.OpenCaptured"],
                            Strings.Current["Diagnostics.RunEvidence.OpenCaptured.Tooltip"],
                            OpenFolderCommand,
                            report.FolderPath)
                    ]));
            }

            RunEvidenceSummary = reports.Count == 0
                ? Strings.Current["Diagnostics.RunEvidence.Empty"]
                : Strings.Current.Plural("Diagnostics.RunEvidence.Summary", reports.Count);
        }

        // Read here rather than from the Play tab's list, so a captured run can be read on a
        // machine where that tab has never been opened.
        private static IReadOnlyList<ModuleEntry> ReadModuleEntries(string install)
        {
            if (string.IsNullOrWhiteSpace(install))
                return [];

            var scan = ModuleScanner.ScanAll(install);

            if (scan.Failed)
                return [];

            var launcher = new LauncherDataStore(LauncherDataStore.GetDefaultPath(ResolveDataRoot()));

            return ModuleEnvironment.Merge(scan, launcher.Exists ? launcher.Read() : []).Entries;
        }

        // Exceptions thrown and handled minutes before a crash. Reading them costs one file, so the
        // scan does it, and the expander refreshes on its own after a watch session.
        public ObservableCollection<KnownIssueRowViewModel> FirstChance { get; } = [];

        [ObservableProperty]
        public partial string FirstChanceSummary { get; set; } = string.Empty;

        [RelayCommand]
        private async Task ReadFirstChanceTraceAsync()
        {
            var trace = await Task.Run(() => FirstChanceTrace.ReadNewest(FirstChancePaths.GetDefaultRoot()));

            FirstChance.Clear();

            if (trace is null)
            {
                FirstChanceSummary = Strings.Current["Diagnostics.FirstChance.None"];
                return;
            }

            FirstChanceSummary = trace.Explain();

            foreach (var record in trace.Records)
            {
                FirstChance.Add(new KnownIssueRowViewModel(
                    record.Describe(),
                    record.Frames.Count == 0
                        ? Strings.Current["Diagnostics.FirstChance.NoFrame"]
                        : string.Join("\n", record.Frames),
                    Strings.Current.Format("Diagnostics.FirstChance.Note", record.Classification),
                    record.Module.Value));
            }

            HasFirstChance = FirstChance.Count > 0;
        }

        // Bisection is demand-driven: it is offered from the crash report that motivated it and from a
        // dry run that failed, never as a destination of its own.
        public ObservableCollection<BisectionSessionRowViewModel> BisectionSessions { get; } = [];

        [ObservableProperty]
        public partial string BisectionReproductionStep { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string BisectionStatus { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string BisectionExperimentText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string BisectionInstruction { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string BisectionQuestion { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string BisectionConclusionText { get; set; } = string.Empty;

        // What the config safety net did across the search. The runner used to throw this away, so a
        // bisect could leave a config changed, or fail to protect one at all, and say neither.
        [ObservableProperty]
        public partial string BisectionConfigReport { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartGuidedBisectionCommand))]
        [NotifyCanExecuteChangedFor(nameof(StartAutomaticBisectionCommand))]
        [NotifyCanExecuteChangedFor(nameof(CancelBisectionCommand))]
        [NotifyCanExecuteChangedFor(nameof(ResumeBisectionCommand))]
        public partial bool IsBisecting { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(LaunchBisectionExperimentCommand))]
        [NotifyCanExecuteChangedFor(nameof(FinishBisectionRunCommand))]
        [NotifyCanExecuteChangedFor(nameof(ReportCrashedCommand))]
        [NotifyCanExecuteChangedFor(nameof(ReportDidNotCrashCommand))]
        [NotifyCanExecuteChangedFor(nameof(ReportToldUsNothingCommand))]
        public partial bool HasPendingExperiment { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ResumeBisectionCommand))]
        [NotifyCanExecuteChangedFor(nameof(DeleteBisectionCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyBisectionSessionCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyBisectionFailingCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyBisectionBackupPathCommand))]
        public partial BisectionSessionRowViewModel? SelectedBisectionSession { get; set; }

        private CancellationTokenSource? bisectionCancellation;
        private TaskCompletionSource<BisectionOutcome>? bisectionAnswer;
        private BisectionExperiment? pendingExperiment;
        private BisectionOutcomeDetector? outcomeDetector;
        private DateTime experimentStartedUtc;

        private bool CanStartGuidedBisection() => !IsBisecting && SelectedReport is not null;

        private bool CanStartAutomaticBisection() => !IsBisecting && DryRunFailed && lastDryRunLoadOrder.Count > 0;

        private bool CanCancelBisection() => IsBisecting;

        private bool CanAnswer() => HasPendingExperiment;

        private bool CanResumeBisection() => !IsBisecting && SelectedBisectionSession is not null;

        private bool HasBisectionSession() => SelectedBisectionSession is not null;

        [RelayCommand(CanExecute = nameof(CanStartGuidedBisection))]
        private Task StartGuidedBisectionAsync() => RunBisectionAsync(BisectionMode.Guided, null);

        [RelayCommand(CanExecute = nameof(CanStartAutomaticBisection))]
        private Task StartAutomaticBisectionAsync() => RunBisectionAsync(BisectionMode.Automatic, null);

        [RelayCommand(CanExecute = nameof(CanResumeBisection))]
        private Task ResumeBisectionAsync() => RunBisectionAsync(BisectionMode.Guided, SelectedBisectionSession!.Snapshot);

        [RelayCommand(CanExecute = nameof(CanCancelBisection))]
        private void CancelBisection()
        {
            BisectionStatus = Strings.Current["Diagnostics.Bisection.Stopping"];
            bisectionAnswer?.TrySetCanceled();
            bisectionCancellation?.Cancel();
        }

        [RelayCommand(CanExecute = nameof(HasBisectionSession))]
        private void DeleteBisection()
        {
            var store = new BisectionStore(BisectionStore.GetDefaultRoot(ResolveDataRoot()));
            var id = SelectedBisectionSession!.Snapshot.Id;

            BisectionStatus = store.Delete(id)
                ? Strings.Current.Format("Diagnostics.Bisection.Deleted", id)
                : Strings.Current.Format("Diagnostics.Bisection.AlreadyGone", id);

            ListBisectionSessions();
        }

        [RelayCommand]
        private void ListBisectionSessions()
        {
            var reading = SelectedBisectionSession?.Snapshot.Id;

            BisectionSessions.Clear();

            foreach (var snapshot in new BisectionStore(BisectionStore.GetDefaultRoot(ResolveDataRoot())).List())
                BisectionSessions.Add(new BisectionSessionRowViewModel(snapshot));

            SelectedBisectionSession = BisectionSessions.FirstOrDefault(row => row.Snapshot.Id == reading)
                ?? BisectionSessions.FirstOrDefault();
        }

        [RelayCommand(CanExecute = nameof(CanAnswer))]
        private void LaunchBisectionExperiment()
        {
            if (pendingExperiment is null)
                return;

            var environment = ShellViewModels.Instance.Environment;

            experimentStartedUtc = DateTime.UtcNow;

            // The experiment's own set, not the load order with its flags rewritten. A direct launch
            // takes its module list on the command line and loads everything on it, so a full list
            // with flags is a full launch.
            BisectionStatus = environment.LaunchModules(
                pendingExperiment.ModulesToLaunch(environment.BuildEnvironment().Entries));
        }

        // BEM decides without asking when it can: a new crash report matching the signature settles the
        // run. "No report appeared" and "it did not crash" are different statements, so when nothing
        // appeared it asks rather than assuming.
        [RelayCommand(CanExecute = nameof(CanAnswer))]
        private void FinishBisectionRun()
        {
            if (outcomeDetector?.Detect(experimentStartedUtc) is BisectionOutcome.Reproduced)
            {
                BisectionQuestion = string.Empty;
                BisectionStatus = Strings.Current["Diagnostics.Bisection.AutoReproduced"];
                Answer(BisectionOutcome.Reproduced);
                return;
            }

            BisectionQuestion = outcomeDetector?.Question
                ?? Strings.Current["Diagnostics.Bisection.DefaultQuestion"];
        }

        [RelayCommand(CanExecute = nameof(CanAnswer))]
        private void ReportCrashed() => Answer(BisectionOutcome.Reproduced);

        [RelayCommand(CanExecute = nameof(CanAnswer))]
        private void ReportDidNotCrash() => Answer(BisectionOutcome.NotReproduced);

        [RelayCommand(CanExecute = nameof(CanAnswer))]
        private void ReportToldUsNothing() => Answer(BisectionOutcome.Invalid);

        private void Answer(BisectionOutcome outcome)
        {
            BisectionQuestion = string.Empty;
            HasPendingExperiment = false;
            bisectionAnswer?.TrySetResult(outcome);
        }

        // A search over a load order this size starts from about two hundred modules and re-derives
        // whatever the user already worked out by hand, at one launch of a several-minute boot per
        // step. A scope is that work handed over instead: the search runs inside the set they
        // chose, and everything outside it stays on from the first run to the last.
        public ObservableCollection<BisectionScopeRowViewModel> BisectionScopeRows { get; } = [];

        public ObservableCollection<BisectionObservationRowViewModel> BisectionObservations { get; } = [];

        private readonly List<BisectionScopeRowViewModel> bisectionScopeCandidates = [];

        [ObservableProperty]
        public partial string BisectionScopeFilter { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string BisectionScopeSummary { get; set; } = Strings.Current["Diagnostics.Bisection.NoScope"];

        [ObservableProperty]
        public partial string BisectionObservationPreview { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ClearBisectionScopeCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyBisectionScopeCommand))]
        [NotifyCanExecuteChangedFor(nameof(RecordBisectionRunCrashedCommand))]
        [NotifyCanExecuteChangedFor(nameof(RecordBisectionRunDidNotCrashCommand))]
        public partial bool HasBisectionScope { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ForgetBisectionObservationCommand))]
        public partial BisectionObservationRowViewModel? SelectedBisectionObservation { get; set; }

        private bool CanClearBisectionScope() => HasBisectionScope && !IsBisecting;

        private bool CanRecordBisectionRun() => HasBisectionScope && !IsBisecting;

        private bool HasSelectedObservation() => SelectedBisectionObservation is not null;

        [RelayCommand]
        private void LoadBisectionScopeCandidates()
        {
            var environment = ShellViewModels.Instance.Environment;

            if (!environment.IsLoaded)
            {
                BisectionScopeSummary = Strings.Current["Diagnostics.Bisection.LoadEnvironmentForScope"];
                return;
            }

            var chosen = new HashSet<ModuleId>(
                bisectionScopeCandidates.Where(row => row.IsInScope).Select(row => row.Id));

            var entries = environment.BuildEnvironment().Entries.Where(entry => !entry.IsOrphan).ToList();
            var tiers = ModuleTierMap.For(entries);

            bisectionScopeCandidates.Clear();

            for (var i = 0; i < entries.Count; i++)
            {
                // The same rule the search itself uses. Harmony, ButterLib, UIExtenderEx, MCM and the
                // game's own modules stay on throughout, so offering them here would be offering a
                // choice no experiment could act on.
                if (tiers[i] is ModuleTier.CrashHandler or ModuleTier.Infrastructure or ModuleTier.Official)
                    continue;

                var row = new BisectionScopeRowViewModel(entries[i]) { IsInScope = chosen.Contains(entries[i].Id) };

                row.PropertyChanged += OnBisectionScopeRowChanged;
                bisectionScopeCandidates.Add(row);
            }

            ShowBisectionScopeRows();
        }

        private void OnBisectionScopeRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(BisectionScopeRowViewModel.IsInScope))
            {
                // A mod taken out of the scope cannot be part of a run recorded over the scope either,
                // so its answer goes with it rather than lingering as a tick nobody can see.
                if (sender is BisectionScopeRowViewModel { IsInScope: false } row)
                    row.WasOn = false;

                DescribeBisectionScope();
            }

            if (e.PropertyName is nameof(BisectionScopeRowViewModel.WasOn))
                DescribeBisectionObservation();
        }

        private void ShowBisectionScopeRows()
        {
            var filter = BisectionScopeFilter.Trim();

            BisectionScopeRows.Clear();

            foreach (var row in bisectionScopeCandidates)
            {
                var matches = filter.Length == 0
                    || row.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || row.Detail.Contains(filter, StringComparison.OrdinalIgnoreCase);

                // A mod already in the scope always stays visible. Filtering one out of sight while it
                // is still part of the set is how a scope gets started with a member the user forgot
                // was in it, and every mistake there costs a launch.
                if (matches || row.IsInScope)
                    BisectionScopeRows.Add(row);
            }

            DescribeBisectionScope();
        }

        partial void OnBisectionScopeFilterChanged(string value)
        {
            _ = value;

            ShowBisectionScopeRows();
        }

        private void DescribeBisectionScope()
        {
            var inScope = bisectionScopeCandidates.Where(row => row.IsInScope).ToList();

            HasBisectionScope = inScope.Count > 0;

            BisectionScopeSummary = inScope.Count == 0
                ? Strings.Current.Plural("Diagnostics.Bisection.ScopeSummary.None", bisectionScopeCandidates.Count)
                : Strings.Current.Plural(
                      "Diagnostics.Bisection.ScopeSummary.Some", inScope.Count, string.Join(", ", inScope.Select(row => row.Detail)))
                  + " " + Strings.Current.Plural(
                      "Diagnostics.Bisection.ScopeSummary.StayOn", bisectionScopeCandidates.Count - inScope.Count);

            DescribeBisectionObservation();
        }

        private void DescribeBisectionObservation()
        {
            var inScope = bisectionScopeCandidates.Where(row => row.IsInScope).ToList();

            if (inScope.Count == 0)
            {
                BisectionObservationPreview = string.Empty;
                return;
            }

            var on = inScope.Where(row => row.WasOn).Select(row => row.Detail).ToList();
            var off = inScope.Where(row => !row.WasOn).Select(row => row.Detail).ToList();

            // Spelled out in full before either button is pressed. A recorded outcome that describes
            // the wrong configuration is worse than no record at all: it sends the search into a half
            // the fault is not in and nothing later in the search recovers from that.
            BisectionObservationPreview = on.Count == 0
                ? Strings.Current.Plural("Diagnostics.Bisection.ObservationPreview.AllOff", off.Count)
                : Strings.Current.Format(
                    "Diagnostics.Bisection.ObservationPreview.Mixed", string.Join(", ", on), string.Join(", ", off));
        }

        [RelayCommand(CanExecute = nameof(CanClearBisectionScope))]
        private void ClearBisectionScope()
        {
            foreach (var row in bisectionScopeCandidates)
            {
                row.IsInScope = false;
                row.WasOn = false;
            }

            // The recorded outcomes describe configurations of a scope that no longer exists, so they
            // go with it. Keeping them would leave a record whose meaning silently changed.
            BisectionObservations.Clear();
            SelectedBisectionObservation = null;
            DescribeBisectionScope();

            BisectionStatus = Strings.Current["Diagnostics.Bisection.ScopeCleared"];
        }

        [RelayCommand(CanExecute = nameof(CanRecordBisectionRun))]
        private void RecordBisectionRunCrashed() => RecordBisectionRun(reproduced: true);

        [RelayCommand(CanExecute = nameof(CanRecordBisectionRun))]
        private void RecordBisectionRunDidNotCrash() => RecordBisectionRun(reproduced: false);

        private void RecordBisectionRun(bool reproduced)
        {
            var inScope = bisectionScopeCandidates.Where(row => row.IsInScope).ToList();
            var on = inScope.Where(row => row.WasOn).Select(row => row.Id).ToList();
            var observation = new BisectionObservation(on, reproduced);

            var existing = BisectionObservations.FirstOrDefault(
                row => new HashSet<ModuleId>(row.Observation.Enabled).SetEquals(on));

            // Two records disagreeing about the same configuration is not a tie a search can break, so
            // the second one replaces the first rather than sitting beside it. The old line is quoted
            // back, because silently overwriting an answer is how a wrong one hides.
            if (existing is not null)
            {
                var was = existing.Title;

                BisectionObservations.Remove(existing);
                BisectionStatus = existing.Observation.Reproduced == reproduced
                    ? Strings.Current.Format("Diagnostics.Bisection.AlreadyRecorded", was)
                    : Strings.Current.Format("Diagnostics.Bisection.ReplacedRecorded", was);
            }
            else
            {
                BisectionStatus = Strings.Current["Diagnostics.Bisection.Recorded"];
            }

            BisectionObservations.Add(new BisectionObservationRowViewModel(observation));
            SelectedBisectionObservation = BisectionObservations[^1];
        }

        [RelayCommand(CanExecute = nameof(HasSelectedObservation))]
        private void ForgetBisectionObservation()
        {
            if (SelectedBisectionObservation is not { } row)
                return;

            BisectionObservations.Remove(row);
            SelectedBisectionObservation = BisectionObservations.FirstOrDefault();
            BisectionStatus = Strings.Current.Format("Diagnostics.Bisection.Forgot", row.Title);
        }

        [RelayCommand(CanExecute = nameof(CanCopyBisectionScope))]
        private void CopyBisectionScope() => Copy(
            string.Join(
                Environment.NewLine,
                bisectionScopeCandidates.Where(row => row.IsInScope).Select(row => row.Detail)),
            Strings.Current.Plural(
                "Diagnostics.Copy.What.ModuleIds", bisectionScopeCandidates.Count(row => row.IsInScope)));

        private bool CanCopyBisectionScope() => HasBisectionScope;

        private IReadOnlyList<ModuleId> ChosenScope() =>
            [.. bisectionScopeCandidates.Where(row => row.IsInScope).Select(row => row.Id)];

        private async Task RunBisectionAsync(BisectionMode mode, BisectionSnapshot? resume)
        {
            var environment = ShellViewModels.Instance.Environment;

            if (!environment.IsLoaded)
            {
                BisectionStatus = Strings.Current["Diagnostics.Bisection.LoadEnvironmentForSearch"];
                return;
            }

            var loadOrder = environment.BuildEnvironment().Entries.Where(e => !e.IsOrphan).ToList();

            var ranked = resume is not null
                ? resume.Ranked.Select(id => new ModuleId(id)).ToList()
                : [.. SelectedReport?.Attribution.Suspects.Select(s => s.ModuleId) ?? []];

            // Null rather than an empty list when nothing was chosen: an empty scope is a scope that
            // named nothing, which the engine refuses, and "no scope" has to stay the search BEM has
            // always run. Resume takes both off the snapshot instead, so a scoped search stays scoped.
            var scope = resume is null && HasBisectionScope ? ChosenScope() : null;

            var known = resume is null
                ? BisectionObservations.Select(row => row.Observation).ToList()
                : [];

            var request = new BisectionRequest(
                loadOrder,
                ranked,
                mode,
                BisectionReproductionStep.Trim(),
                Scope: scope,
                KnownOutcomes: known);

            var session = resume is null
                ? BisectionSession.Start(request)
                : BisectionSession.Resume(resume, request);

            if (session is null)
            {
                BisectionStatus = Strings.Current["Diagnostics.Bisection.CouldNotResume"];
                return;
            }

            if (session.Conclusion.Result is BisectionResult.RefusedEmptyScope)
            {
                // A scope that named nothing a search could turn off. Refused before anything is
                // launched or written, and said plainly rather than started and abandoned.
                BisectionConclusionText = string.Join(
                    "\n",
                    new[] { session.Conclusion.Headline }.Concat(session.Conclusion.Detail));

                return;
            }

            var install = ResolveInstallPath();

            outcomeDetector = mode is BisectionMode.Guided && SelectedReport is not null
                ? new BisectionOutcomeDetector(
                    CrashReportLocator.DefaultFolders(install, ResolveDataRoot()),
                    CrashSignature.Of(SelectedReport.Report.Report))
                : null;

            var launcherDataPath = LauncherDataStore.GetDefaultPath(ResolveDataRoot());

            // The per-experiment snapshot only lives as long as BEM does. The backup store is what
            // makes the pre-search order reachable from a cold start, which is the state a guided run
            // is left in whenever BEM is closed or crashes while the user is playing.
            //
            // The selected version's ring, like the file the search is writing. Taken from the machine
            // ring it filed the copy of one version's LauncherData.xml among another version's backups:
            // the version being searched then listed no backup at all, which is the one thing that puts
            // its order back after a cold start, while the other version listed one that would write a
            // foreign module list over its real load order.
            var runner = new BisectionRunner(
                Path.Combine(DryRunPaths.GetDefaultSnapshotRoot(), "bisect"),
                launcherDataPath,
                Path.GetDirectoryName(launcherDataPath) ?? string.Empty,
                new LoadOrderBackupStore(LoadOrderBackupStore.GetDefaultRoot(ResolveDataRoot())));

            var oracle = mode is BisectionMode.Guided
                ? GuidedOracle(loadOrder, launcherDataPath)
                : AutomaticOracle(environment, install, loadOrder, launcherDataPath);

            if (oracle is null)
                return;

            using var cancellation = new CancellationTokenSource();

            bisectionCancellation = cancellation;
            IsBisecting = true;
            BisectionConclusionText = string.Empty;
            BisectionConfigReport = string.Empty;
            BisectionStatus = (mode is BisectionMode.Guided
                    ? Strings.Current["Diagnostics.Bisection.Started.Guided"]
                    : Strings.Current["Diagnostics.Bisection.Started.Automatic"])
                + DescribeScope(session);

            var progress = new Progress<BisectionExperiment>(experiment =>
            {
                pendingExperiment = experiment;
                BisectionExperimentText = experiment.Describe();
                BisectionInstruction = experiment.Instruction;
            });

            BisectionConclusion conclusion;

            try
            {
                // One activation around the whole search, not one per experiment. Every run in it
                // starts the game, and on a version that is not the resting one that game has to see
                // that version's saves, Configs, logs and shader cache; without this the search ran
                // against whichever version rests and reported the answer about the selected one.
                //
                // The scope cannot be per experiment. A guided run hands the game to the user and
                // comes back only when they answer, so a per-experiment scope would be released while
                // they were still playing, and ActivationLock refuses a second activation inside a
                // live one anyway. The junctions come down when this returns, whether it returns a
                // conclusion, throws, or is canceled.
                conclusion = await environment.RunUnderActiveVersionAsync(
                    ct => runner.RunAsync(
                        session,
                        oracle,
                        progress,
                        new BisectionStore(BisectionStore.GetDefaultRoot(ResolveDataRoot())),
                        ct),
                    cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                BisectionStatus = Strings.Current["Diagnostics.Bisection.Stopped"];
                return;
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "The bisection failed");
                BisectionStatus = Strings.Current.Format("Diagnostics.Bisection.CouldNotComplete", ex.Message);
                return;
            }
            finally
            {
                bisectionCancellation = null;
                bisectionAnswer = null;
                pendingExperiment = null;
                HasPendingExperiment = false;
                BisectionQuestion = string.Empty;
                IsBisecting = false;
                BisectionConfigReport = DescribeConfigOutcomes(runner.ConfigOutcomes);
                ListBisectionSessions();
            }

            BisectionExperimentText = string.Empty;
            BisectionInstruction = string.Empty;
            BisectionConclusionText = string.Join("\n", new[] { conclusion.Headline }.Concat(conclusion.Detail));
            BisectionStatus = Strings.Current.Plural(
                "Diagnostics.Bisection.Conclusion", conclusion.Experiments, $"{conclusion.ReproductionRate:P0}")
                + DescribeScope(session)
                + DescribeRecalled(runner.Recalled);
        }

        private static string DescribeScope(BisectionSession session) => session.IsScoped
            ? " " + Strings.Current.Plural(
                  "Diagnostics.Bisection.DescribeScope.Chosen",
                  session.Scope.Count,
                  string.Join(", ", session.Scope.Select(m => m.Value)))
              + " " + Strings.Current.Plural("Diagnostics.Bisection.DescribeScope.StayOn", session.OutsideScope.Count)
            : string.Empty;

        // Named rather than absorbed. A search that answered three of its own questions from what the
        // owner had already run is a different thing to read than one that spent three launches, and
        // it is also the only place those recorded answers are visible after the fact.
        private static string DescribeRecalled(IReadOnlyList<BisectionExperiment> recalled) =>
            recalled.Count == 0
                ? string.Empty
                : " " + Strings.Current.Plural(
                    "Diagnostics.Bisection.DescribeRecalled",
                    recalled.Count,
                    string.Join(", ", recalled.Select(e => e.Number)));

        private static string DescribeConfigOutcomes(IReadOnlyList<LaunchConfigRestoreResult> outcomes)
        {
            if (outcomes.Count == 0)
                return string.Empty;

            // Each run described on its own rather than merged, and numbered by where it actually
            // came in the search: what a run left behind is only worth reading next to which run
            // left it.
            var interesting = outcomes
                .Select((outcome, index) => (Outcome: outcome, Number: index + 1))
                .Where(pair => !pair.Outcome.IsClean)
                .ToList();

            if (interesting.Count == 0)
                return Strings.Current.Plural("Diagnostics.Bisection.ConfigOutcomes.Clean", outcomes.Count);

            return Strings.Current["Diagnostics.Bisection.ConfigOutcomes.Prefix"] + " " + string.Join(
                " ",
                interesting.Select(pair => Strings.Current.Format(
                    "Diagnostics.Bisection.ConfigOutcomes.Run", pair.Number, string.Join(" ", pair.Outcome.Describe()))));
        }

        // Guided mode writes the configuration and then waits for the user. The runner has already
        // snapshotted LauncherData.xml and Configs and puts them back the moment this returns.
        private BisectionOracle GuidedOracle(IReadOnlyList<ModuleEntry> loadOrder, string launcherDataPath)
        {
            var environment = ShellViewModels.Instance.Environment.BuildEnvironment();

            return async (experiment, cancellationToken) =>
            {
                var entries = loadOrder
                    .Select(e => e with { IsEnabled = experiment.Enabled.Contains(e.Id) })
                    .ToList();

                new LauncherDataStore(launcherDataPath).Write(environment.WithEntries(entries).ToLauncherEntries());

                var answer = new TaskCompletionSource<BisectionOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                bisectionAnswer = answer;
                experimentStartedUtc = DateTime.UtcNow;

                // The runner is off the UI thread by the time the oracle is called, and the buttons
                // that answer it are bound to this flag, so it is set where the bindings live. It is
                // set only after the configuration is on disk: a Launch that ran first would start
                // the previous run's load order.
                App.AppWindow?.DispatcherQueue.TryEnqueue(() => HasPendingExperiment = true);

                using var registration = cancellationToken.Register(() => answer.TrySetCanceled());

                return await answer.Task;
            };
        }

        private BisectionOracle? AutomaticOracle(
            EnvironmentViewModel environment,
            string install,
            IReadOnlyList<ModuleEntry> loadOrder,
            string launcherDataPath)
        {
            var payload = CompanionPayload.LocateForInstall(install);

            if (!payload.Found)
            {
                BisectionStatus = Strings.Current.Format("Diagnostics.Bisection.NoAutomaticSearch", payload.Reason);
                return null;
            }

            // A search whose every launch records nothing would otherwise report every module innocent.
            if (!payload.MatchesInstallPlatform)
                BisectionStatus = payload.Reason;

            var orchestrator = new DryRunOrchestrator(
                DryRunPaths.GetDefaultRoot(),
                DryRunPaths.GetDefaultSnapshotRoot(),
                DryRunProcessLauncher.Create());

            var template = new DryRunRequest(
                install,
                loadOrder,
                payload.Path!,
                launcherDataPath,
                Path.GetDirectoryName(launcherDataPath) ?? string.Empty,
                environment.ExtraArguments,
                environment.PreferredTarget,
                environment.CrashHandling);

            var progress = new Progress<DryRunProgress>(p => BisectionStatus = p.Message);

            return DryRunBisection.Oracle(orchestrator, template, loadOrder, progress);
        }

        [RelayCommand(CanExecute = nameof(CanScan))]
        private async Task ScanLogsAndCrashReportsAsync()
        {
            IsBusy = true;

            var install = ResolveInstallPath();
            GameInstallPath = install;

            try
            {
                var enabled = EnabledModules();
                var found = await Task.Run(() => Collect(install, enabled));

                Reports.Clear();

                foreach (var row in found.Rows)
                    Reports.Add(row);

                reportCandidates = found.Candidates;
                reportRoots = found.Search.Searched;

                // Read as part of the same pass, because "no crash report was found" is a false
                // statement when the game left a whole crash folder on disk, and that sentence is what
                // sent the user away from a complete crash believing BEM had nothing. It is also what
                // carries the command line each report's narrowing is intersected against.
                var folders = found.Folders;

                GameCrashFolderRows.Clear();

                foreach (var folder in folders.Folders)
                {
                    GameCrashFolderRows.Add(new GameCrashFolderRowViewModel(
                        folder, CopyGameCrashFolderCommand, OpenGameCrashFolderCommand));
                }

                GameCrashFoldersStatus = folders.Describe();

                SearchedFolders = Strings.Current.Format(
                    "Diagnostics.Scan.LookedIn",
                    string.Join("; ", found.Search.Searched.Concat(folders.Searched).Distinct(StringComparer.OrdinalIgnoreCase)));

                StatusMessage = Describe(found.Search, install, folders);
                SelectedReport = Reports.FirstOrDefault();

                ShowRegistry(found.Registry, found.Collisions);
                ShowCaptures();
            }
            catch (Exception ex)
            {
                StatusMessage = Strings.Current.Format("Diagnostics.Scan.CrashFoldersFailed", ex.Message);
            }

            // The logs are read in their own try so that a crash folder BEM cannot open does not cost
            // the user the logs, and the other way round.
            try
            {
                var logs = await Task.Run(() => ReadLogs(install));

                allLogs.Clear();
                allLogs.AddRange(logs.Rows);
                lastLogs = logs.Search.Logs;

                // A captured artifact is BEM's own copy of evidence that no longer exists anywhere
                // else, so it is never offered up to be cleared.
                logCandidates =
                [
                    .. logs.Search.Logs
                        .Where(log => log.Source != LogSource.Captured)
                        .Select(log => new ClearCandidate(log.Path, log.SizeBytes))
                ];

                // The capture store is in this list on purpose. It is what decides whether a row's own
                // "Clear this log" is allowed to touch the file, and a captured copy the user asked to
                // clear by name was being refused as "not in a folder BEM searched". The bulk clears
                // are driven by their candidate lists, not by this one, so they still leave captures
                // alone unless the item that names them is used.
                logRoots = logs.Search.Searched;
                LogFolders = Strings.Current.Format("Diagnostics.Scan.LookedIn", string.Join("; ", logs.Search.Searched));
                LogsStatus = Describe(logs.Search);
                ShowLogs();
            }
            catch (Exception ex)
            {
                LogsStatus = Strings.Current.Format("Diagnostics.Scan.LogFoldersFailed", ex.Message);
            }

            // A third try of its own, for the same reason the logs have theirs: a dump BEM cannot open
            // must not cost the user the crash reports, and the other way round.
            try
            {
                await ShowCrashDumpsAsync();
            }
            catch (Exception ex)
            {
                CrashDumpsStatus = Strings.Current.Format(
                    "Diagnostics.Scan.CrashDumpsFailed", Core.Diagnostics.CrashDumps.GetDefaultFolder(), ex.Message);
            }

            // A fourth try of its own. Reading an operating system log through a native interop layer
            // is the one thing on this page BEM does not control the input of, and it must not be able
            // to cost the user the dumps, the logs or the crash reports.
            try
            {
                await ShowWindowsFaultsAsync();
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to read the Windows Application event log");
                WindowsFaultsStatus = Strings.Current.Format("Diagnostics.Scan.WindowsFaultLogFailed", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }

            await ReadFirstChanceTraceAsync();

            ListBisectionSessions();
        }

        private bool CanScan() => !IsBusy;

        partial void OnShowOnlyLogsWithFindingsChanged(bool value)
        {
            _ = value;

            ShowLogs();
        }

        // Clearing the collection drives the list's selection to null, so the row the user was reading
        // is put back afterwards whenever the filter still lets it through.
        private void ShowLogs()
        {
            var reading = SelectedLog;

            Logs.Clear();

            foreach (var row in allLogs.Where(row => !ShowOnlyLogsWithFindings || LogFilter.HasFindings(row.Scan)))
                Logs.Add(row);

            SelectedLog = reading is not null && Logs.Contains(reading) ? reading : Logs.FirstOrDefault();
            LogFilterSummary = LogFilter.Describe(allLogs.Count(row => LogFilter.HasFindings(row.Scan)), allLogs.Count);
        }

        private static (LogSearch Search, List<LogRowViewModel> Rows) ReadLogs(string install)
        {
            var search = LogLocator.Find(install, ResolveDataRoot());

            var rows = search.Logs
                .Select(log => new LogRowViewModel(log, LogScanner.Scan(log.Path)))
                .ToList();

            return (search, rows);
        }

        [RelayCommand(CanExecute = nameof(CanScan))]
        private Task ClearLogsAsync() => ClearAsync(
            "Diagnostics.Clear.CountedNoun.Log",
            "Diagnostics.Clear.BatchReason.Logs",
            "logs",
            logCandidates,
            logRoots);

        [RelayCommand(CanExecute = nameof(CanScan))]
        private Task ClearCrashReportsAsync() => ClearAsync(
            "Diagnostics.Clear.CountedNoun.CrashReport",
            "Diagnostics.Clear.BatchReason.CrashReports",
            "crash reports",
            reportCandidates,
            reportRoots);

        [RelayCommand(CanExecute = nameof(CanScan))]
        private Task ClearLogsAndCrashReportsAsync() => ClearAsync(
            "Diagnostics.Clear.CountedNoun.LogAndCrashReport",
            "Diagnostics.Clear.BatchReason.LogsAndCrashReports",
            "logs and crash reports",
            [.. logCandidates, .. reportCandidates],
            [.. logRoots, .. reportRoots]);

        // BEM's own copies are the only surviving record of a crash the game deleted, so no sweep of the
        // game's files ever takes them along. They still have to be clearable by hand or the store grows
        // without limit and one crash's dump is hundreds of megabytes, so this is the item that names
        // them and moves nothing else. It moves rather than deletes, like every other clear here.
        [RelayCommand(CanExecute = nameof(CanScan))]
        private Task ClearCapturesAsync() => ClearAsync(
            "Diagnostics.Clear.CountedNoun.CapturedCopy",
            "Diagnostics.Clear.BatchReason.CapturedCopies",
            "captured copies",
            captureCandidates,
            [CaptureRoot]);

        // Three nouns, not one. nounKey names a counted noun, selected on the number of files it stands
        // for, which is what makes it agree in a language that declines a noun after a numeral.
        // reasonKey names the same category with no number at all: it is written into the batch index and
        // read back later as the label of the whole batch, where a count would be a different batch's.
        // logNoun is always an English literal, kept apart from the localized nouns shown on screen and
        // in dialogs: the log line is read by BEM's own author in a bug report and a translated word
        // there makes that report harder to act on, not easier, the same rule every other log line in
        // this file follows.
        private async Task ClearAsync(
            string nounKey,
            string reasonKey,
            string logNoun,
            IReadOnlyList<ClearCandidate> files,
            IReadOnlyList<string> roots)
        {
            if (files.Count == 0)
            {
                StatusMessage = Strings.Current.Format(
                    "Diagnostics.Clear.NothingToMove", Strings.Current.Plural(nounKey, 0));
                return;
            }

            if (!await ConfirmClearAsync(nounKey, ClearedFileStore.Measure(files)))
                return;

            IsBusy = true;

            ClearResult result;

            try
            {
                var store = new ClearedFileStore(ClearedFileStore.GetDefaultRoot(ResolveDataRoot()));

                result = await Task.Run(() =>
                    store.Clear(files, roots, Strings.Current[reasonKey], DateTimeOffset.Now));
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, $"Failed to clear the {logNoun}");
                StatusMessage = Strings.Current.Format("Diagnostics.Clear.NothingMoved", ex.Message);
                return;
            }
            finally
            {
                IsBusy = false;
            }

            // Re-reading disk is what keeps the lists from offering a file that is no longer there,
            // and it is also the only honest way to show what a locked file left behind.
            await ScanLogsAndCrashReportsAsync();

            StatusMessage = result.Describe();
        }

        private static async Task<bool> ConfirmClearAsync(string nounKey, ClearScope scope)
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Current.Format("Diagnostics.Clear.Confirm.Title", scope.Describe(nounKey)),
                Content = new TextBlock
                {
                    Text = Strings.Current.Format(
                        "Diagnostics.Clear.Confirm.Body", ClearedFileStore.GetDefaultRoot(ResolveDataRoot())),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = Strings.Current["Diagnostics.Clear.Confirm.PrimaryButton"],
                CloseButtonText = Strings.Current["Diagnostics.CancelButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        // The other half of Clear Files. Everything a clear moved is listed here, put back from here, and
        // deleted only from here, so the reversal sits next to the action rather than in the file system.
        [RelayCommand(CanExecute = nameof(CanScan))]
        private async Task ListClearedBatchesAsync()
        {
            var store = new ClearedFileStore(ClearedFileStore.GetDefaultRoot(ResolveDataRoot()));

            ClearedBatchList listed;

            try
            {
                listed = await Task.Run(store.List);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to list the cleared batches");
                ClearedStoreStatus = Strings.Current.Format(
                    "Diagnostics.Clear.CouldNotReadStore", ClearedFileStore.GetDefaultRoot(ResolveDataRoot()), ex.Message);
                return;
            }

            var reading = SelectedClearedBatch?.BatchPath;

            ClearedBatches.Clear();

            foreach (var batch in listed.Batches)
                ClearedBatches.Add(new ClearedBatchRowViewModel(batch));

            SelectedClearedBatch = ClearedBatches.FirstOrDefault(row => row.BatchPath == reading)
                ?? ClearedBatches.FirstOrDefault();

            ClearedStoreStatus = Strings.Current.Format(
                "Diagnostics.Clear.StoreLocation", listed.Describe(), ClearedFileStore.GetDefaultRoot(ResolveDataRoot()));
        }

        partial void OnSelectedClearedBatchChanged(ClearedBatchRowViewModel? value)
        {
            ClearedBatchFiles.Clear();

            if (value is null)
                return;

            foreach (var file in value.Batch.Files)
                ClearedBatchFiles.Add(new ClearedFileRowViewModel(file));

            SelectedClearedFile = ClearedBatchFiles.FirstOrDefault();
        }

        [RelayCommand(CanExecute = nameof(HasClearedBatch))]
        private Task RestoreClearedBatchAsync() => RestoreAsync(SelectedClearedBatch!, null);

        [RelayCommand(CanExecute = nameof(HasClearedFile))]
        private Task RestoreClearedFileAsync() => RestoreAsync(SelectedClearedBatch!, SelectedClearedFile!.File);

        private async Task RestoreAsync(ClearedBatchRowViewModel batch, ClearedFile? file)
        {
            IsBusy = true;

            RestoreResult result;

            try
            {
                var store = new ClearedFileStore(ClearedFileStore.GetDefaultRoot(ResolveDataRoot()));
                var stamp = DateTimeOffset.Now;

                result = await Task.Run(() => file is null
                    ? store.Restore(batch.BatchPath, stamp)
                    : store.Restore(batch.BatchPath, [file], stamp));
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to restore a cleared batch");
                ClearedStoreStatus = Strings.Current.Format("Diagnostics.Clear.NothingRestored", ex.Message);
                return;
            }
            finally
            {
                IsBusy = false;
            }

            await ScanLogsAndCrashReportsAsync();
            await ListClearedBatchesAsync();

            StatusMessage = result.Describe();
            ClearedStoreStatus = result.Describe();
        }

        [RelayCommand(CanExecute = nameof(HasClearedBatch))]
        private async Task DiscardClearedBatchAsync()
        {
            var batch = SelectedClearedBatch!;
            var store = new ClearedFileStore(ClearedFileStore.GetDefaultRoot(ResolveDataRoot()));
            var scope = store.MeasureBatch(batch.BatchPath);

            if (!await ConfirmDiscardAsync(batch, scope))
                return;

            IsBusy = true;

            DiscardResult result;

            try
            {
                result = await Task.Run(() => store.Discard(batch.BatchPath, RecycleFolder));
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to discard a cleared batch");
                ClearedStoreStatus = Strings.Current.Format("Diagnostics.Clear.NothingDeleted", ex.Message);
                return;
            }
            finally
            {
                IsBusy = false;
            }

            await ListClearedBatchesAsync();

            ClearedStoreStatus = result.Removed
                ? Strings.Current.Format("Diagnostics.Clear.InRecycleBin", result.Describe())
                : result.Describe();
        }

        // The count and the size are read off disk rather than off the index, because this is the one
        // action here that takes a batch out of BEM's reach and the number has to be the real one.
        private static async Task<bool> ConfirmDiscardAsync(ClearedBatchRowViewModel batch, ClearScope scope)
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Current.Format(
                    "Diagnostics.Clear.ConfirmDiscard.Title", scope.Describe("Diagnostics.Clear.CountedNoun.ClearedFile")),
                Content = new TextBlock
                {
                    Text = Strings.Current.Format(
                        "Diagnostics.Clear.ConfirmDiscard.Body", batch.BatchPath, batch.Origins),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = Strings.Current["Diagnostics.Clear.ConfirmDiscard.PrimaryButton"],
                CloseButtonText = Strings.Current["Diagnostics.CancelButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        private bool HasClearedBatch() => !IsBusy && SelectedClearedBatch is not null;

        private bool HasClearedFile() => !IsBusy && SelectedClearedBatch is not null && SelectedClearedFile is not null;

        // Which row a right-click landed on is recorded before the flyout opens, so the items can bind
        // the page's own commands rather than one set of commands per row.
        public LogRowViewModel? ContextLog { get; private set; }

        public CrashReportRowViewModel? ContextReport { get; private set; }

        public KnownIssueRowViewModel? ContextIssue { get; private set; }

        // Navigating a Frame is the page's job, not a view model's, so the page listens for this.
        public event Action? EnvironmentTabRequested;

        public void SetContextLog(LogRowViewModel? row)
        {
            ContextLog = row;
            OpenLogCommand.NotifyCanExecuteChanged();
            ShowLogInFolderCommand.NotifyCanExecuteChanged();
            CopyLogPathCommand.NotifyCanExecuteChanged();
            CopyLogPassagesCommand.NotifyCanExecuteChanged();
            ClearLogCommand.NotifyCanExecuteChanged();
        }

        public void SetContextReport(CrashReportRowViewModel? row)
        {
            ContextReport = row;
            OpenReportCommand.NotifyCanExecuteChanged();
            ShowReportInFolderCommand.NotifyCanExecuteChanged();
            CopyReportPathCommand.NotifyCanExecuteChanged();
            CopyReportFindingsCommand.NotifyCanExecuteChanged();
            ClearReportCommand.NotifyCanExecuteChanged();
        }

        public void SetContextIssue(KnownIssueRowViewModel? row)
        {
            ContextIssue = row;
            CopyIssueCommand.NotifyCanExecuteChanged();
            OpenIssueModuleFolderCommand.NotifyCanExecuteChanged();
            ShowIssueModuleOnPlayTabCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand(CanExecute = nameof(HasContextLog))]
        private void OpenLog() => OpenInEditor(ContextLog!.Log.Path);

        [RelayCommand(CanExecute = nameof(HasContextLog))]
        private void ShowLogInFolder() => ShowInFolder(ContextLog!.Log.Path);

        [RelayCommand(CanExecute = nameof(HasContextLog))]
        private void CopyLogPath() => Copy(ContextLog!.Log.Path, Strings.Current["Diagnostics.Copy.What.Path"]);

        [RelayCommand(CanExecute = nameof(CanCopyLogPassages))]
        private void CopyLogPassages() => Copy(
            string.Join(
                "\n\n",
                ContextLog!.Scan.Findings.Select(f => Strings.Current.Format(
                    "Diagnostics.Copy.LineLabel", f.LineNumber, string.Join("\n", f.Excerpt)))),
            Strings.Current.Plural("Diagnostics.Copy.What.Passages", ContextLog!.Scan.Findings.Count));

        [RelayCommand(CanExecute = nameof(HasContextLog))]
        private Task ClearLogAsync() => ClearAsync(
            "Diagnostics.Clear.CountedNoun.Log",
            "Diagnostics.Clear.BatchReason.Logs",
            "log",
            [new ClearCandidate(ContextLog!.Log.Path, ContextLog!.Log.SizeBytes)],
            logRoots);

        [RelayCommand(CanExecute = nameof(HasContextReport))]
        private void OpenReport() => OpenInEditor(ContextReport!.Report.Path);

        [RelayCommand(CanExecute = nameof(HasContextReport))]
        private void ShowReportInFolder() => ShowInFolder(ContextReport!.Report.Path);

        [RelayCommand(CanExecute = nameof(HasContextReport))]
        private void CopyReportPath() => Copy(ContextReport!.Report.Path, Strings.Current["Diagnostics.Copy.What.Path"]);

        [RelayCommand(CanExecute = nameof(HasContextReport))]
        private void CopyReportFindings() =>
            Copy(Summarize(ContextReport!), Strings.Current["Diagnostics.Copy.What.ReportFindings"]);

        [RelayCommand(CanExecute = nameof(HasContextReport))]
        private Task ClearReportAsync() => ClearAsync(
            "Diagnostics.Clear.CountedNoun.CrashReport",
            "Diagnostics.Clear.BatchReason.CrashReports",
            "crash report",
            [new ClearCandidate(ContextReport!.Report.Path, SizeOf(ContextReport!.Report.Path))],
            reportRoots);

        [RelayCommand(CanExecute = nameof(HasContextIssue))]
        private void CopyIssue() => Copy(ContextIssue!.Text, Strings.Current["Diagnostics.Copy.What.Issue"]);

        [RelayCommand(CanExecute = nameof(HasIssueModuleFolder))]
        private void OpenIssueModuleFolder() => Start(
            new System.Diagnostics.ProcessStartInfo(ContextIssue!.ModuleFolderPath) { UseShellExecute = true },
            Strings.Current.Format("Diagnostics.Opened", ContextIssue!.ModuleFolderPath));

        [RelayCommand(CanExecute = nameof(HasIssueModule))]
        private void ShowIssueModuleOnPlayTab()
        {
            ShellViewModels.Instance.Environment.SearchText = ContextIssue!.ModuleId;
            EnvironmentTabRequested?.Invoke();
        }

        // A row whose text opts out of selection so the click can pick it still carries paths that exist
        // to be pasted somewhere else, so the row's own menu is what copies them.
        [RelayCommand(CanExecute = nameof(HasBisectionSession))]
        private void CopyBisectionSession() => Copy(
            SelectedBisectionSession!.Title + Environment.NewLine + SelectedBisectionSession!.Detail,
            Strings.Current["Diagnostics.Copy.What.Search"]);

        [RelayCommand(CanExecute = nameof(CanCopyBisectionFailing))]
        private void CopyBisectionFailing() => Copy(
            string.Join(Environment.NewLine, SelectedBisectionSession!.Snapshot.Failing),
            Strings.Current.Plural("Diagnostics.Copy.What.ModuleIds", SelectedBisectionSession!.Snapshot.Failing.Count));

        [RelayCommand(CanExecute = nameof(CanCopyBisectionBackup))]
        private void CopyBisectionBackupPath() => Copy(
            SelectedBisectionSession!.Snapshot.SafetyBackupPath,
            Strings.Current["Diagnostics.Copy.What.LoadOrderBackupPath"]);

        [RelayCommand(CanExecute = nameof(HasSelectedClearedBatch))]
        private void CopyClearedBatch() => Copy(
            SelectedClearedBatch!.Title + Environment.NewLine + SelectedClearedBatch!.Origins,
            Strings.Current["Diagnostics.Copy.What.Batch"]);

        [RelayCommand(CanExecute = nameof(HasSelectedClearedBatch))]
        private void CopyClearedBatchPath() =>
            Copy(SelectedClearedBatch!.BatchPath, Strings.Current["Diagnostics.Copy.What.BatchFolderPath"]);

        [RelayCommand(CanExecute = nameof(CanCopyClearedBatchOrigins))]
        private void CopyClearedBatchOrigins() => Copy(
            string.Join(Environment.NewLine, SelectedClearedBatch!.Batch.Files.Select(file => file.OriginPath)),
            Strings.Current.Plural(
                "Diagnostics.Copy.What.OriginalPaths", SelectedClearedBatch!.Batch.Files.Count));

        [RelayCommand(CanExecute = nameof(HasSelectedClearedFile))]
        private void CopyClearedFile() => Copy(
            SelectedClearedFile!.Title + Environment.NewLine + SelectedClearedFile!.Origin,
            Strings.Current["Diagnostics.Copy.What.File"]);

        [RelayCommand(CanExecute = nameof(HasSelectedClearedFile))]
        private void CopyClearedFileOrigin() =>
            Copy(SelectedClearedFile!.File.OriginPath, Strings.Current["Diagnostics.Copy.What.OriginalPath"]);

        [RelayCommand(CanExecute = nameof(HasSelectedClearedFile))]
        private void CopyClearedFileStoredPath() => Copy(
            SelectedClearedFile!.File.StoredPath,
            Strings.Current["Diagnostics.Copy.What.KeptAtPath"]);

        private bool CanCopyBisectionFailing() => SelectedBisectionSession is { Snapshot.Failing.Count: > 0 };

        private bool CanCopyBisectionBackup() => SelectedBisectionSession is { Snapshot.SafetyBackupPath.Length: > 0 };

        private bool HasSelectedClearedBatch() => SelectedClearedBatch is not null;

        private bool CanCopyClearedBatchOrigins() => SelectedClearedBatch is { Batch.Files.Count: > 0 };

        private bool HasSelectedClearedFile() => SelectedClearedFile is not null;

        private bool HasContextLog() => ContextLog is not null;

        private bool CanCopyLogPassages() => ContextLog is { Scan.Findings.Count: > 0 };

        private bool HasContextReport() => ContextReport is not null;

        private bool HasContextIssue() => ContextIssue is not null;

        private bool HasIssueModule() => ContextIssue is { ModuleId.Length: > 0 };

        private bool HasIssueModuleFolder() =>
            ContextIssue is { ModuleFolderPath.Length: > 0 } row && Directory.Exists(row.ModuleFolderPath);

        private static string Summarize(CrashReportRowViewModel row)
        {
            var lines = new List<string> { row.Title, row.Verdict, DescribeFault(row) };

            lines.AddRange(row.Attribution.Suspects.Select(s =>
                $"{s.ModuleId} - {s.Band.ToString().ToUpperInvariant()} (rule {s.Rule}): {s.Evidence}"));

            return string.Join("\n", lines.Where(l => l.Length > 0));
        }

        // Notepad++ is what a modder actually reads a log in, but it is not always installed, so the
        // shell default takes over when neither path is there.
        private static readonly string[] TextEditors =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Notepad++", "notepad++.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Notepad++", "notepad++.exe")
        ];

        private void OpenInEditor(string path)
        {
            if (!File.Exists(path))
            {
                StatusMessage = Strings.Current.Format("Diagnostics.NoLongerOnDisk", path);
                return;
            }

            var editor = TextEditors.FirstOrDefault(File.Exists);

            Start(
                editor is null
                    ? new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }
                    : new System.Diagnostics.ProcessStartInfo(editor, [path]),
                Strings.Current.Format("Diagnostics.Opened", path));
        }

        private void ShowInFolder(string path)
        {
            if (!File.Exists(path))
            {
                StatusMessage = Strings.Current.Format("Diagnostics.NoLongerOnDisk", path);
                return;
            }

            Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe")
                {
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = false
                },
                Strings.Current.Format("Diagnostics.ShowedInFolder", path));
        }

        private void Start(System.Diagnostics.ProcessStartInfo startInfo, string success)
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(startInfo);
                StatusMessage = success;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                LoggingService.LogException(ex, "Failed to open a diagnostics path");
                StatusMessage = Strings.Current.Format("Diagnostics.CouldNotOpen", startInfo.FileName, ex.Message);
            }
        }

        // Whatever else has the clipboard open (a clipboard manager, an RDP session) makes this throw,
        // and an unhandled throw here would close the app.
        private void Copy(string text, string what)
        {
            if (string.IsNullOrEmpty(text))
            {
                StatusMessage = Strings.Current["Diagnostics.Copy.Nothing"];
                return;
            }

            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);

            try
            {
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                StatusMessage = Strings.Current.Format("Diagnostics.Copy.Success", what);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to copy diagnostics text to the clipboard");
                StatusMessage = Strings.Current["Diagnostics.Copy.Failed"];
            }
        }

        // A bug report the user can post on a mod's page. BEM drafts it and never sends it: there is no
        // endpoint here, no account, and no button that publishes anything. It is offered from the two
        // places the evidence actually lives, the crash report and the search that isolated the mod,
        // because a report drafted anywhere else would be drafted from less than BEM has.
        public ObservableCollection<BugReportSubjectViewModel> BugReportSubjects { get; } = [];

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DraftBugReportCommand))]
        public partial BugReportSubjectViewModel? SelectedBugReportSubject { get; set; }

        [ObservableProperty]
        public partial string BugReportStatus { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string BugReportTitle { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string BugReportQualityText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string BugReportPreview { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CopyBugReportMarkdownCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyBugReportBBCodeCommand))]
        [NotifyCanExecuteChangedFor(nameof(SaveBugReportCommand))]
        [NotifyCanExecuteChangedFor(nameof(DiscardBugReportCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyBugReportTitleCommand))]
        public partial bool BugReportReady { get; set; }

        // Which markup is on screen. The two carry the same words, so this changes nothing about what
        // the report says; it changes where it can be pasted.
        [ObservableProperty]
        public partial bool BugReportShowsBBCode { get; set; }

        [ObservableProperty]
        public partial bool BugReportBusy { get; set; }

        private ModBugReport? draftedBugReport;

        private HarmonyPatchTargetReport? lastHarmonyPatchTargets;

#if DEV_BEM
        private DecompiledMethod? lastDecompiled;
#endif

        private bool CanDraftBugReport() => SelectedBugReportSubject is not null && !BugReportBusy;

        private bool HasDraftedBugReport() => BugReportReady;

        partial void OnBugReportShowsBBCodeChanged(bool value)
        {
            if (draftedBugReport is { } report)
                BugReportPreview = value ? report.BBCode : report.Markdown;
        }

        // Every mod a report could honestly be about, strongest first: the ones a search named, then the
        // ones the crash report ranked, then everything else installed. The last group is there because
        // the user may want to write about a mod no evidence on this machine has ranked yet, and a
        // picker that refused would be BEM deciding for them what they are allowed to report.
        [RelayCommand]
        private void ListBugReportSubjects()
        {
            var reading = SelectedBugReportSubject?.Id;
            var environment = ShellViewModels.Instance.Environment;
            var entries = environment.IsLoaded ? environment.BuildEnvironment().Entries : [];
            var seen = new HashSet<ModuleId>();

            BugReportSubjects.Clear();

            void Add(ModuleId id, string why)
            {
                if (id.IsEmpty || !seen.Add(id))
                    return;

                var entry = entries.FirstOrDefault(e => e.Id == id);

                BugReportSubjects.Add(new BugReportSubjectViewModel(
                    id,
                    entry?.DisplayName ?? id.Value,
                    why));
            }

            if (SelectedBisectionSession?.Snapshot is { } snapshot
                && string.Equals(snapshot.Result, nameof(BisectionResult.Caused), StringComparison.Ordinal))
            {
                foreach (var id in snapshot.Failing)
                    Add(new ModuleId(id), Strings.Current["Diagnostics.BugReport.Why.NamedBySearch"]);
            }

            foreach (var suspect in SelectedReport?.Attribution.Suspects ?? [])
                Add(suspect.ModuleId, Strings.Current["Diagnostics.BugReport.Why.RankedSuspect"]);

            foreach (var entry in entries
                         .Where(entry => !entry.IsOfficial && entry.Manifest is not null)
                         .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                Add(entry.Id, Strings.Current["Diagnostics.BugReport.Why.NoEvidenceYet"]);
            }

            SelectedBugReportSubject = BugReportSubjects.FirstOrDefault(row => row.Id == reading)
                ?? BugReportSubjects.FirstOrDefault();

            BugReportStatus = BugReportSubjects.Count == 0
                ? Strings.Current["Diagnostics.BugReport.LoadEnvironment"]
                : Strings.Current.Plural("Diagnostics.BugReport.SubjectCount", BugReportSubjects.Count);
        }

        [RelayCommand(CanExecute = nameof(CanDraftBugReport))]
        private async Task DraftBugReportAsync()
        {
            if (SelectedBugReportSubject is not { } chosen)
                return;

            var environment = ShellViewModels.Instance.Environment;

            if (!environment.IsLoaded)
            {
                BugReportStatus = Strings.Current["Diagnostics.BugReport.LoadEnvironment"];
                return;
            }

            BugReportBusy = true;

            try
            {
                var install = ResolveInstallPath();
                var loadOrder = environment.BuildEnvironment().Entries.Where(e => !e.IsOrphan).ToList();
                var crash = SelectedReport;
                var snapshot = SelectedBisectionSession?.Snapshot;
                var finding = StaticFindingFor(chosen.Id);
                var decompiled = DecompiledFor(chosen.Id, loadOrder);
                var step = BisectionReproductionStep.Trim();

                var report = await Task.Run(() => BugReportDrafter.Draft(new BugReportRequest(
                    chosen.Id,
                    loadOrder,
                    ReadEnvironmentForReport(install),
                    ReadAssembliesFor(chosen.Id, loadOrder),
                    ReadCrashForReport(crash),
                    snapshot,
                    decompiled,
                    finding,
                    step)));

                draftedBugReport = report;
                BugReportTitle = report.Title;
                BugReportQualityText = Describe(report.Quality);
                BugReportPreview = BugReportShowsBBCode ? report.BBCode : report.Markdown;
                BugReportReady = true;

                BugReportStatus = Strings.Current["Diagnostics.BugReport.Drafted"];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LoggingService.LogException(ex, "Failed to draft a bug report");
                BugReportStatus = Strings.Current.Format("Diagnostics.BugReport.CouldNotDraft", ex.Message);
            }
            finally
            {
                BugReportBusy = false;
            }
        }

        private static string Describe(BugReportQuality quality)
        {
            var strength = quality.Strength switch
            {
                BugReportStrength.Strong => Strings.Current["Diagnostics.BugReport.Strength.Strong"],
                BugReportStrength.Moderate => Strings.Current["Diagnostics.BugReport.Strength.Moderate"],
                _ => Strings.Current["Diagnostics.BugReport.Strength.Thin"]
            };

            var lines = new List<string> { $"{strength}: {quality.Headline}" };

            if (quality.Present.Count > 0)
                lines.Add(Strings.Current.Format("Diagnostics.BugReport.Carries", string.Join(", ", quality.Present)));

            if (quality.Missing.Count > 0)
                lines.Add(Strings.Current.Format("Diagnostics.BugReport.DoesNotCarry", string.Join(", ", quality.Missing)));

            lines.Add(quality.WouldStrengthen);

            return string.Join(" ", lines);
        }

        private static BugReportEnvironment ReadEnvironmentForReport(string install)
        {
            var libraries = new List<BugReportLibrary>();
            var entries = ShellViewModels.Instance.Environment.BuildEnvironment().Entries;

            // The four the community asks about before anything else. Named only when installed.
            string[] wanted =
            [
                "Bannerlord.Harmony", "Bannerlord.ButterLib", "Bannerlord.UIExtenderEx", "Bannerlord.MBOptionScreen"
            ];

            foreach (var id in wanted)
            {
                if (entries.FirstOrDefault(entry => entry.Id == new ModuleId(id)) is { Manifest: not null } found)
                    libraries.Add(new BugReportLibrary(id, found.Version.ToString()));
            }

            return new BugReportEnvironment(
                GameVersionReader.Read(install).ToString(),
                GameInstallLocator.DetectPlatform(install).PlatformFolder ?? string.Empty,
                WindowsName(),
                LastLaunchTarget(),
                libraries);
        }

        // ProductName still reads "Windows 10" on Windows 11, which is Microsoft's own quirk and not
        // something a mod author should have to correct for. The build number is what separates them.
        private static string WindowsName()
        {
            var architecture = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";

            try
            {
                var name = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                    "ProductName",
                    null) as string;

                if (string.IsNullOrWhiteSpace(name))
                    return $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {architecture}";

                if (Environment.OSVersion.Version.Build >= 22000)
                    name = name.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);

                return $"{name}, {architecture}";
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {architecture}";
            }
        }

        // How the game was last started, read off BEM's own launch log. Empty when BEM has no record,
        // because "started some other way" and "started through BLSE" are different statements.
        private static string LastLaunchTarget()
        {
            var history = new LaunchHistoryStore(LaunchHistoryStore.GetDefaultPath(ResolveDataRoot())).Read();

            return history.Count == 0 ? string.Empty : history[^1].TargetName;
        }

        private static BugReportCrash? ReadCrashForReport(CrashReportRowViewModel? row)
        {
            if (row is null)
                return null;

            var report = row.Report.Report;

            if (report.Root is not { } root)
                return null;

            return new BugReportCrash(
                root.TypeFullName,
                root.Message,
                [.. root.Frames.Select(frame => frame.Text.Trim())],
                EnhancedStacktrace.Parse(report.RawText));
        }

        private static IReadOnlyList<BugReportAssembly> ReadAssembliesFor(
            ModuleId id,
            IReadOnlyList<ModuleEntry> loadOrder)
        {
            if (loadOrder.FirstOrDefault(entry => entry.Id == id)?.Manifest is not { } manifest)
                return [];

            var folder = Path.Combine(manifest.FolderPath, "bin");

            try
            {
                if (!Directory.Exists(folder))
                    return [];

                return
                [
                    .. Directory
                        .EnumerateFiles(folder, "*.dll", System.IO.SearchOption.AllDirectories)
                        .Select(path => new FileInfo(path))
                        .OrderByDescending(file => file.Length)
                        .Take(6)
                        .Select(file => new BugReportAssembly(file.Name, file.Length, file.LastWriteTimeUtc))
                ];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        // A static finding about this module, if the known-issue sweep has already run. Never scanned
        // on the click: the sweep reads every assembly on the install and takes seconds.
        private string StaticFindingFor(ModuleId id)
        {
            if (lastHarmonyPatchTargets is not { } report)
                return string.Empty;

            var finding = report.Findings.FirstOrDefault(target => target.ModuleId == id)
                ?? report.Undecidable.FirstOrDefault(target => target.ModuleId == id);

            return finding?.Describe() ?? string.Empty;
        }

        // The second seam. Only the method the user actually asked BEM to read back, and only when it
        // came out of this module's own assembly: a body from somebody else's DLL is not evidence about
        // this mod. Nothing outside the ring decompiles, so there is never a body to attach and the
        // drafter writes the same report without one.
        private DecompiledMethod? DecompiledFor(ModuleId id, IReadOnlyList<ModuleEntry> loadOrder)
        {
#if DEV_BEM
            if (lastDecompiled is not { Succeeded: true, AssemblyPath.Length: > 0 } method)
                return null;

            if (loadOrder.FirstOrDefault(entry => entry.Id == id)?.Manifest is not { } manifest)
                return null;

            return method.AssemblyPath.StartsWith(manifest.FolderPath, StringComparison.OrdinalIgnoreCase)
                ? method
                : null;
#else
            _ = id;
            _ = loadOrder;

            return null;
#endif
        }

        [RelayCommand(CanExecute = nameof(HasDraftedBugReport))]
        private void CopyBugReportMarkdown() =>
            Copy(draftedBugReport?.Markdown ?? string.Empty, Strings.Current["Diagnostics.Copy.What.ReportMarkdown"]);

        [RelayCommand(CanExecute = nameof(HasDraftedBugReport))]
        private void CopyBugReportBBCode() =>
            Copy(draftedBugReport?.BBCode ?? string.Empty, Strings.Current["Diagnostics.Copy.What.ReportBBCode"]);

        [RelayCommand(CanExecute = nameof(HasDraftedBugReport))]
        private void CopyBugReportTitle() => Copy(BugReportTitle, Strings.Current["Diagnostics.Copy.What.ReportTitle"]);

        [RelayCommand(CanExecute = nameof(HasDraftedBugReport))]
        private async Task SaveBugReportAsync()
        {
            if (draftedBugReport is not { } report)
                return;

            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                SuggestedFileName = report.SuggestedFileName
            };

            picker.FileTypeChoices.Add(Strings.Current["Diagnostics.BugReport.Save.MarkdownChoice"], [".md"]);
            picker.FileTypeChoices.Add(Strings.Current["Diagnostics.BugReport.Save.BBCodeChoice"], [".txt"]);

            if (App.AppWindow is not { } window)
            {
                BugReportStatus = Strings.Current["Diagnostics.BugReport.Save.NoPicker"];
                return;
            }

            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(window));

            var file = await picker.PickSaveFileAsync();

            if (file is null)
                return;

            var bbcode = file.FileType.Equals(".txt", StringComparison.OrdinalIgnoreCase);

            try
            {
                await File.WriteAllTextAsync(file.Path, bbcode ? report.BBCode : report.Markdown);

                BugReportStatus = Strings.Current.Format(
                    "Diagnostics.BugReport.Saved", bbcode ? "BBCode" : "Markdown", file.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to save a bug report");
                BugReportStatus = Strings.Current.Format("Diagnostics.BugReport.CouldNotSave", ex.Message);
            }
        }

        // Both directions. A drafted report stays on screen until something takes it off again.
        [RelayCommand(CanExecute = nameof(HasDraftedBugReport))]
        private void DiscardBugReport()
        {
            draftedBugReport = null;
            BugReportPreview = string.Empty;
            BugReportTitle = string.Empty;
            BugReportQualityText = string.Empty;
            BugReportReady = false;
            BugReportStatus = Strings.Current["Diagnostics.BugReport.Discarded"];
        }

#if DEV_BEM
        private CancellationTokenSource? decompileCancellation;

        // The outer ring. Never on page load and never for a whole assembly: one method, when the user
        // asks for it, on a thread pool thread, with a cancel next to it. A real install indexes 621
        // assemblies and some mod DLLs are megabytes.
        //
        // Reading a decompiled body is the author's move: a player is served by the ranked suspects,
        // the frame attribution and the crash reading, which every build keeps.
        [RelayCommand(CanExecute = nameof(CanDecompile))]
        private Task ShowFaultingMethodAsync()
        {
            if (SelectedReport is null)
            {
                DecompiledSource = string.Empty;
                DecompileStatus = Strings.Current["Diagnostics.Decompile.NoReportSelected"];
                return Task.CompletedTask;
            }

            var fault = SelectedReport.Attribution.FaultFrame;

            if (fault is null)
            {
                DecompiledSource = string.Empty;
                DecompileStatus = Strings.Current["Diagnostics.Decompile.NoStackTrace"];
                return Task.CompletedTask;
            }

            return DecompileAsync(fault);
        }

        [RelayCommand(CanExecute = nameof(CanDecompileSelectedFrame))]
        private Task DecompileSelectedFrameAsync() => DecompileAsync(SelectedFrame!.Frame);

        private async Task DecompileAsync(CrashFrame frame)
        {
            var install = ResolveInstallPath();

            if (string.IsNullOrWhiteSpace(install))
            {
                DecompiledSource = string.Empty;
                DecompileStatus = Strings.Current["Diagnostics.Decompile.NoInstall"];
                return;
            }

            decompileCancellation?.Cancel();
            decompileCancellation?.Dispose();

            using var cancellation = new CancellationTokenSource();
            decompileCancellation = cancellation;

            IsDecompiling = true;
            DecompiledSource = string.Empty;
            DecompileStatus = Strings.Current.Format("Diagnostics.Decompile.Reading", frame.QualifiedName);

            try
            {
                var result = await Task.Run(
                    () => MethodDecompiler.DecompileFrame(EnsureIndex(install), frame, cancellation.Token),
                    cancellation.Token);

                DecompiledSource = result.Source;
                DecompiledAssemblyPath = result.AssemblyPath;
                lastDecompiled = result;

                DecompileStatus = result.OtherAssemblies.Count == 0
                    ? result.Message
                    : result.Message + " " + Strings.Current.Plural(
                        "Diagnostics.Decompile.OtherAssemblies",
                        result.OtherAssemblies.Count,
                        string.Join("; ", result.OtherAssemblies));
            }
            catch (OperationCanceledException)
            {
                DecompiledSource = string.Empty;
                DecompileStatus = Strings.Current["Diagnostics.Decompile.Stopped"];
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to decompile a method");
                DecompiledSource = string.Empty;
                DecompileStatus = Strings.Current.Format("Diagnostics.Decompile.Failed", frame.QualifiedName, ex.Message);
            }
            finally
            {
                IsDecompiling = false;

                if (ReferenceEquals(decompileCancellation, cancellation))
                    decompileCancellation = null;
            }
        }

        // Stop belongs to the two buttons it stops.
        [RelayCommand(CanExecute = nameof(IsDecompiling))]
        private void CancelDecompile() => decompileCancellation?.Cancel();

        private bool CanDecompile() => !IsDecompiling;

        private bool CanDecompileSelectedFrame() => !IsDecompiling && SelectedFrame is not null;

        // dotPeek opens the assembly, not the method: it has no command line that navigates to one. Saying
        // so is the difference between a shortcut and a promise BEM cannot keep.
        [RelayCommand(CanExecute = nameof(CanOpenInDotPeek))]
        private void OpenInDotPeek() => Start(
            new System.Diagnostics.ProcessStartInfo(DotPeekPath, [DecompiledAssemblyPath]),
            Strings.Current.Format("Diagnostics.Decompile.OpenedInDotPeek", DecompiledAssemblyPath));

        private bool CanOpenInDotPeek() => HasDotPeek && DecompiledAssemblyPath.Length > 0;
#endif

        // The two places the rest of this view model meets the decompiler, and the only two that
        // change shape between the tiers. Both are here, small and named, rather than as conditions
        // inside the methods that call them: choosing a report and drafting a bug report read the same
        // in either build, and neither leaves a field that only one tier can ever assign.

        // Rebuilt whenever the selected report changes, so the dropdown always lists the frames of the
        // report on screen. Listing them costs nothing: it is text already parsed out of the report.
        // Nothing here decompiles until the user asks for a method by name.
        private void ResetDecompileFor(CrashReportRowViewModel? value)
        {
#if DEV_BEM
            Frames.Clear();
            SelectedFrame = null;
            DecompiledSource = string.Empty;
            DecompiledAssemblyPath = string.Empty;
            DecompileStatus = string.Empty;

            if (value is null)
                return;

            foreach (var frame in value.Attribution.Frames)
                Frames.Add(new CrashFrameRowViewModel(frame));

            SelectedFrame = Frames.FirstOrDefault();
#else
            _ = value;
#endif
        }

        // The one part of the folded-body remedy BEM may apply by itself. It reorders and nothing else:
        // no file is moved, no module is switched off, and the Play tab's Undo covers it.
        // Deliberately without the tab switch and the search filter the button does. This runs off a scan
        // the user did not ask for, so it corrects the order and says so; it does not move them.
        private void ApplyNativeFirstAutomatically(RigOrderFix plan)
        {
            var environment = ShellViewModels.Instance.Environment;

            if (!environment.IsLoaded || plan.AnchorId is not { } anchor)
                return;

            var moved = string.Join(", ", plan.AboveAnchor.Select(id => id.Value));

            environment.ApplyFix(new LoadOrderIssue(
                IssueKind.ContentBeforeNative,
                IssueSeverity.Warning,
                anchor,
                null,
                Strings.Current.Format("Diagnostics.Rig.MustLoadBefore", anchor, moved),
                Fix: current =>
                {
                    var recomputed = RigConflicts.PlanNativeFirst(current.Entries);

                    return recomputed.IsNeeded ? current.WithEntries(recomputed.Corrected) : current;
                }));

            CanApplyRigOrderFix = false;
            RigOrderStatusText = Strings.Current.Format("Diagnostics.Rig.MovedAutomatically", anchor, moved);
            KnownIssuesStatus = RigOrderStatusText;
        }

        [RelayCommand(CanExecute = nameof(CanApplyRigOrderFix))]
        private async Task ApplyRigOrderFixAsync()
        {
            var environment = ShellViewModels.Instance.Environment;

            if (!environment.IsLoaded)
            {
                KnownIssuesStatus = Strings.Current["Diagnostics.KnownIssues.LoadModulesForOrder"];
                return;
            }

            var entries = CurrentEntries(environment);
            var plan = await Task.Run(() => RigConflicts.PlanNativeFirst(entries));

            if (!plan.IsNeeded)
            {
                RigOrderStatusText = Describe(plan);
                CanApplyRigOrderFix = false;
                KnownIssuesStatus = Strings.Current["Diagnostics.Rig.AlreadySatisfied"];
                return;
            }

            var moved = string.Join(", ", plan.AboveAnchor.Select(id => id.Value));

            environment.ApplyFix(new LoadOrderIssue(
                IssueKind.ContentBeforeNative,
                IssueSeverity.Warning,
                plan.AnchorId!.Value,
                null,
                Strings.Current.Format("Diagnostics.Rig.MustLoadBefore", plan.AnchorId, moved),
                Fix: current =>
                {
                    var recomputed = RigConflicts.PlanNativeFirst(current.Entries);

                    return recomputed.IsNeeded ? current.WithEntries(recomputed.Corrected) : current;
                }));

            environment.SearchText = plan.AnchorId.Value.Value;
            EnvironmentTabRequested?.Invoke();

            CanApplyRigOrderFix = false;
            RigOrderStatusText = Strings.Current.Format("Diagnostics.Rig.MovedByOwner", plan.AnchorId, moved);
            KnownIssuesStatus = RigOrderStatusText;
        }

        // The other half is the user's call, so it is a button per mod rather than a fix BEM applies. It
        // changes one enabled flag on the Play tab and touches no file belonging to either mod.
        [RelayCommand]
        private void DisableRigModule(string? moduleId)
        {
            if (string.IsNullOrWhiteSpace(moduleId))
                return;

            var environment = ShellViewModels.Instance.Environment;

            if (!environment.IsLoaded)
            {
                KnownIssuesStatus = Strings.Current["Diagnostics.KnownIssues.LoadModulesForToggle"];
                return;
            }

            var row = environment.Modules.FirstOrDefault(m =>
                string.Equals(m.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));

            if (row is null)
            {
                KnownIssuesStatus = Strings.Current.Format("Diagnostics.Rig.NotInList", moduleId);
                return;
            }

            if (!row.IsEnabled)
            {
                KnownIssuesStatus = Strings.Current.Format("Diagnostics.Rig.AlreadyDisabled", row.DisplayName);
                return;
            }

            row.IsEnabled = false;
            environment.SearchText = row.ModuleId;
            EnvironmentTabRequested?.Invoke();

            KnownIssuesStatus = Strings.Current.Format("Diagnostics.Rig.Disabled", row.DisplayName);

            // A disabled module can resolve findings across several categories at once - a rig
            // conflict, a shadowed assembly, a Harmony patch overlap - and CheckInstallAsync is the only
            // thing that recomputes InstallCheckFindingCount, which the shell's Health badge and the
            // Health page both read. Not awaited: the status line above already gave immediate feedback,
            // and running the full check under it would make an instant "written straight away" fix feel
            // like a 20-second one. CheckInstallAsync catches its own exceptions and reports them through
            // KnownIssuesStatus, so nothing here is silently lost by not awaiting it.
            _ = CheckInstallAsync();
        }

        // Every load-order remedy this tab offers. It goes through the Play tab's own fix path,
        // so the undo stack, the live write and the constraint check all apply, and the
        // plan refuses outright rather than write an order that breaks a declared edge.
        [RelayCommand]
        private void ApplyLoadOrderMove(LoadOrderMove? move)
        {
            if (move is null)
                return;

            var environment = ShellViewModels.Instance.Environment;

            if (!environment.IsLoaded)
            {
                KnownIssuesStatus = Strings.Current["Diagnostics.KnownIssues.LoadModulesForOrder"];
                return;
            }

            var mover = new ModuleId(move.MoverId);
            var anchor = new ModuleId(move.AnchorId);
            var plan = LoadOrderRemedies.PlanMoveBelow(CurrentEntries(environment), mover, anchor);

            if (!plan.IsReady)
            {
                KnownIssuesStatus = plan.Message;
                return;
            }

            environment.ApplyFix(new LoadOrderIssue(
                IssueKind.OrderViolation,
                IssueSeverity.Warning,
                mover,
                anchor,
                plan.Message,
                Fix: current =>
                {
                    var recomputed = LoadOrderRemedies.PlanMoveBelow(current.Entries, mover, anchor);

                    return recomputed.IsReady ? current.WithEntries(recomputed.Corrected) : current;
                }));

            environment.SearchText = move.MoverId;
            EnvironmentTabRequested?.Invoke();

            KnownIssuesStatus = plan.Message + " " + Strings.Current["Diagnostics.Rig.WrittenStraightAway"];

            // Same reasoning as DisableRigModule: a reorder can resolve the rig conflict, the XML
            // overlap, or whatever finding proposed it, and InstallCheckFindingCount only comes from a
            // full CheckInstallAsync. Fired rather than awaited so the fix stays instant; the count and
            // the finding list catch up moments later on their own.
            _ = CheckInstallAsync();
        }

        [RelayCommand]
        private void OpenFolder(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (!Directory.Exists(path))
            {
                KnownIssuesStatus = Strings.Current.Format("Diagnostics.NoLongerOnDisk", path);
                return;
            }

            Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }, Strings.Current.Format("Diagnostics.Opened", path));
        }

        private static string Stubs(RigPatcher patcher) => patcher.StubAnimations.Count == 0
            ? Strings.Current["Diagnostics.Rig.NoneOnDisk"]
            : string.Join(", ", patcher.StubAnimations
                .Take(3)
                .Select(s => $"{s.AnimationName} points at {s.SourceClipName} in {s.SizeBytes} bytes"))
              + (patcher.StubAnimations.Count > 3
                  ? Strings.Current.Plural("Diagnostics.Rig.AndMore", patcher.StubAnimations.Count - 3)
                  : string.Empty);

        private static IReadOnlyList<ModuleEntry> CurrentEntries(EnvironmentViewModel environment) =>
            [.. environment.Modules.Select(row => row.Entry with { IsEnabled = row.IsEnabled })];

        private static string Describe(RigOrderFix fix) => fix.Status switch
        {
            RigOrderStatus.NoRegistrants => Strings.Current["Diagnostics.Rig.Describe.NoRegistrants"],
            RigOrderStatus.AnchorMissing =>
                Strings.Current.Plural("Diagnostics.Rig.Describe.AnchorMissing", fix.Registrants.Count),
            RigOrderStatus.AnchorFirst =>
                Strings.Current.Plural(
                    "Diagnostics.Rig.Describe.AnchorFirst", fix.Registrants.Count, fix.AnchorId),
            _ => Strings.Current.Plural(
                "Diagnostics.Rig.Describe.SortsAbove",
                fix.AboveAnchor.Count,
                string.Join(", ", fix.AboveAnchor.Select(id => id.Value)),
                fix.AnchorId)
        };

        // The known-issue checks read the module folder and nothing else. They do not need a crash
        // report to exist, so they run when the page opens and never through the scan command.
        [RelayCommand(CanExecute = nameof(CanCheckInstall))]
        private async Task CheckInstallAsync()
        {
            if (IsCheckingInstall)
                return;

            IsCheckingInstall = true;

            try
            {
                var install = ResolveInstallPath();
                GameInstallPath = install;

                Show(await Task.Run(() => Inspect(install)));
            }
            catch (Exception ex)
            {
                KnownIssuesStatus = Strings.Current.Format("Diagnostics.KnownIssues.CouldNotRun", ex.Message);
            }
            finally
            {
                IsCheckingInstall = false;
            }
        }

        private bool CanCheckInstall() => !IsCheckingInstall;

        // Runs once when the shell loads, so a count exists on the navigation before anyone goes looking.
        // Only the sub-second checks: the merge passes take half a minute between them and would make
        // starting BEM feel broken. Their panels say they have not been run, and the button runs them.
        //
        // Silent by design. Nothing here writes, nothing steals focus, and a failure leaves the counts
        // absent rather than putting an error in front of someone who has not asked for anything yet.
        public async Task CheckInstallQuicklyAsync()
        {
            if (IsCheckingInstall || HasCheckedInstall)
                return;

            IsCheckingInstall = true;

            try
            {
                // Off the UI thread: with no configured path this falls through to the locator, and
                // the locator's Game Pass fallback walks XboxGames and WindowsApps on every fixed
                // drive. This runs from the shell's OnLoaded, so doing that walk synchronously froze
                // the first frame of the window on exactly the machines where nothing was found.
                var install = await Task.Run(ResolveInstallPath);
                GameInstallPath = install;

                Show(await Task.Run(() => Inspect(install, includeMergePasses: false)));
                HasCheckedInstall = true;
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "The startup install check could not run");
            }
            finally
            {
                IsCheckingInstall = false;
            }
        }

        private bool HasCheckedInstall { get; set; }

        // Both scans want the index and either can run first, so the build happens once under a lock
        // rather than twice on two threads.
        private AssemblyIndex EnsureIndex(string install)
        {
            lock (indexLock)
            {
                // Indexing 500 assemblies takes seconds, and it only changes when the install does.
                if (!string.Equals(indexedInstallPath, install, StringComparison.OrdinalIgnoreCase))
                {
                    index = AssemblyIndex.Build(install);
                    indexedInstallPath = install;
                }

                return index;
            }
        }

        // The index is keyed on the install path, which does not change when BEM itself deletes an
        // assembly out of that install. Without this the check that follows a removal read the index
        // built before it, so every removed copy was still listed, three rows looked like the button
        // had done nothing, and each vanished file printed as "0 bytes" because its path no longer
        // opened. Anything in BEM that changes an assembly on disk has to call this.
        private void InvalidateIndex()
        {
            lock (indexLock)
            {
                index = AssemblyIndex.Empty;
                indexedInstallPath = string.Empty;
            }
        }

        // The module set has to be read off the UI thread's own list before the scan moves to a
        // worker, and it is what keys the patch registry: a registry captured from a different set is
        // handed back labeled stale rather than attributed against.
        private static IReadOnlyList<ModuleId> EnabledModules()
        {
            var environment = ShellViewModels.Instance.Environment;

            if (environment.IsLoaded)
                return [.. environment.BuildEnvironment().Entries.Where(e => e.IsEnabled).Select(e => e.Id)];

            try
            {
                var store = new LauncherDataStore(LauncherDataStore.GetDefaultPath(ResolveDataRoot()));

                return store.Exists
                    ? [.. store.Read().Where(e => e.IsSelected).Select(e => e.Id)]
                    : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                           or System.Xml.XmlException)
            {
                LoggingService.LogException(ex, "Failed to read the enabled modules for the patch registry lookup");
                return [];
            }
        }

        private (CrashReportSearch Search, List<CrashReportRowViewModel> Rows, List<ClearCandidate> Candidates,
            PatchRegistryStatus Registry, PatchCollisionReport Collisions, GameCrashFolderSearch Folders)
            Collect(string install, IReadOnlyList<ModuleId> enabled)
        {
            var assemblies = EnsureIndex(install);
            var gameVersion = GameVersionReader.Read(install) is { IsEmpty: false } version
                ? version.ToString()
                : null;

            var registries = new PatchRegistryStore(PatchRegistryStore.GetDefaultRoot(ResolveDataRoot()));
            var lookup = CrashAnalysis.Look(registries, enabled, gameVersion);

            // Read before the reports rather than after them, because the crash folder is what carries
            // the command line the run launched with, and a report analyzed without it cannot say how
            // far through that order the run got.
            var dataRoot = ResolveDataRoot();
            var folders = GameCrashFolders.Find(install, dataRoot);
            var evidence = ReadEvidence(install, assemblies, folders);

            var search = CrashReportLocator.Find(install, dataRoot);

            var rows = search.Reports
                .Select(report => new CrashReportRowViewModel(
                    report,
                    CrashAnalysis.Analyze(
                        report.Report, assemblies, enabled, lookup, gameVersion, report.Occurrences,
                        evidence: evidence, reportPath: report.Path, written: report.Written)))
                .ToList();

            // A captured artifact is BEM's own copy of evidence that no longer exists anywhere else,
            // so it is never offered up to be cleared.
            var candidates = search.Reports
                .Where(report => report.Source != CrashReportSource.Captured)
                .Select(report => new ClearCandidate(report.Path, SizeOf(report.Path)))
                .ToList();

            return (
                search,
                rows,
                candidates,
                CrashAnalysis.Describe(lookup),
                PatchCollisions.Build(lookup.Registry),
                folders);
        }

        // The two static reads a crash narrowing joins against a load order. Neither is required: a
        // failure here costs the narrowing and nothing else, so it never takes the crash reports with
        // it.
        private static CrashEvidence ReadEvidence(
            string install,
            AssemblyIndex assemblies,
            GameCrashFolderSearch folders)
        {
            try
            {
                var scan = ModuleScanner.ScanAll(install);

                if (scan.Failed)
                    return new CrashEvidence(CrashFolders: folders.Folders);

                var launcher = new LauncherDataStore(LauncherDataStore.GetDefaultPath(ResolveDataRoot()));
                var entries = ModuleEnvironment.Merge(scan, launcher.Exists ? launcher.Read() : []).Entries;

                return new CrashEvidence(
                    HarmonyCallSites.Inspect(assemblies),
                    HarmonyPatchTargets.Inspect(assemblies, entries),
                    folders.Folders);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                           or System.Xml.XmlException)
            {
                LoggingService.LogException(ex, "Failed to read the static evidence a crash narrowing joins");

                return new CrashEvidence(CrashFolders: folders.Folders);
            }
        }

        private static long SizeOf(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }

        private sealed record KnownIssues(
            RigReport Rig,
            IReadOnlyList<ShadowedAssembly> Assemblies,
            IReadOnlyList<DuplicateModule> Shadowed,
            GpuReport Graphics,
            string? ScanError,
            IReadOnlyDictionary<ModuleId, string>? Folders = null,
            XmlOverlapReport? XmlOverlaps = null,
            long XmlOverlapMilliseconds = 0,
            XmlMergeFaultReport? MergeFaults = null,
            long MergeFaultMilliseconds = 0,
            GameVersionSupportReport? VersionSupport = null,
            HarmonyPatchTargetReport? HarmonyPatches = null,
            GameAssemblyShadowReport? GameShadows = null,
            IReadOnlyList<DuplicateModuleIdGroup>? DuplicateIds = null);

        // System.Management resolves at JIT time, so a single-file publish can fail on entry to
        // GpuInfo.Read before its own try block is reached. Without this the whole tab is lost.
        private static GpuReport ReadGraphics()
        {
            if (!OperatingSystem.IsWindows())
                return new GpuReport([], Strings.Current["Diagnostics.Graphics.WindowsOnly"]);

            try
            {
                return GpuInfo.Read();
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Reading the display adapters failed");
                return new GpuReport([], Strings.Current["Diagnostics.Graphics.CouldNotRead"]);
            }
        }

        // The two XML passes merge every dataset on the install and measure about 17 and 19 seconds
        // against 232 modules. Every other check here finishes inside a second altogether, so they are
        // separable and the split is what lets the fast ones run on their own at startup. Skipping the
        // merges leaves their reports null, which the panels already read as "not run on this check"
        // rather than as a clean result.
        private KnownIssues Inspect(string install, bool includeMergePasses = true)
        {
            var graphics = ReadGraphics();

            if (string.IsNullOrWhiteSpace(install))
                return new KnownIssues(new RigReport([], [], []), [], [], graphics, Strings.Current["Diagnostics.KnownIssues.NoInstall"]);

            var scan = ModuleScanner.ScanAll(install);

            if (scan.Failed)
                return new KnownIssues(new RigReport([], [], []), [], [], graphics, scan.Error);

            var launcher = new LauncherDataStore(LauncherDataStore.GetDefaultPath(ResolveDataRoot()));
            var environment = ModuleEnvironment.Merge(scan, launcher.Exists ? launcher.Read() : []);

            var timer = System.Diagnostics.Stopwatch.StartNew();
            var overlaps = includeMergePasses ? XmlDatasetOverlaps.Inspect(environment.Entries) : null;
            timer.Stop();

            var faultTimer = System.Diagnostics.Stopwatch.StartNew();
            var faults = includeMergePasses ? XmlMergeFaults.Inspect(environment.Entries) : null;
            faultTimer.Stop();

            return new KnownIssues(
                RigConflicts.Inspect(environment.Entries),
                DuplicateAssemblies.Find(
                    EnsureIndex(install),
                    environment.Entries,
                    GameInstallLocator.GetBinaryFolder(install)),
                scan.Shadowed,
                graphics,
                null,
                scan.Modules
                    .GroupBy(m => m.Id)
                    .ToDictionary(g => g.Key, g => g.First().FolderPath),
                overlaps,
                timer.ElapsedMilliseconds,
                faults,
                faultTimer.ElapsedMilliseconds,
                GameVersionSupport.Inspect(environment.Entries, GameVersionReader.Read(install)),
                HarmonyPatchTargets.Inspect(EnsureIndex(install), environment.Entries),
                GameAssemblyShadows.Inspect(EnsureIndex(install), environment.Entries),
                scan.DuplicateIds);
        }

        private void Show(KnownIssues issues)
        {
            // Kept so a bug report can quote the static finding for one module without re-reading every
            // assembly on the install, which takes seconds and would have to happen on the click.
            lastHarmonyPatchTargets = issues.HarmonyPatches;

            string Folder(ModuleId id) =>
                issues.Folders is { } folders && folders.TryGetValue(id, out var path) ? path : string.Empty;

            // Loaded once per check rather than per row: the same set applies to every family here, and
            // a check that walks 238 modules is not the place to reopen a file per finding.
            var acceptedKeys = AcceptedFindings.LoadKeys();

            void EnableAccept(KnownIssueRowViewModel row, string category) =>
                row.EnableAccept(category, acceptedKeys.Contains(AcceptedFindingStore.KeyFor(category, row.Heading)), AcceptFinding);

            RigConflictRows.Clear();
            RigIssues.Clear();
            AssemblyIssues.Clear();
            HarmonyPatchIssues.Clear();
            ShadowedModules.Clear();
            DuplicateIds.Clear();
            GraphicsAdapters.Clear();
            VersionGaps.Clear();
            MergeFaults.Clear();
            XmlOverlapConflicts.Clear();
            XmlOverlapOverrides.Clear();
            XmlDatasetProblems.Clear();
            XmlDatasetsLoadedElsewhere.Clear();
            XmlTransforms.Clear();

            foreach (var conflict in issues.Rig.Conflicts)
            {
                var names = string.Join(" and ", conflict.Patchers.Select(p => p.DisplayName));
                var context = conflict.AlsoWriting.Count == 0
                    ? string.Empty
                    : " " + Strings.Current.Plural(
                        "Diagnostics.Rig.Conflict.Context",
                        conflict.AlsoWriting.Count,
                        string.Join(", ", conflict.AlsoWriting.Select(p => p.DisplayName)));

                var conflictRow = new RigConflictRowViewModel(
                    Strings.Current.Format("Diagnostics.Rig.Conflict.Heading", names, conflict.ActionSetId),
                    string.Join(" ", conflict.Patchers.Select(p =>
                        Strings.Current.Plural("Diagnostics.Rig.Conflict.PatcherDetail.Actions", p.Actions.Count, p.DisplayName)
                        + " " + Strings.Current.Plural(
                            "Diagnostics.Rig.Conflict.PatcherDetail.Animations", p.StubAnimations.Count, Stubs(p))))
                      + context,
                    Strings.Current.Format("Diagnostics.Rig.Conflict.Note", conflict.AppliedLast.DisplayName),
                    [.. conflict.Patchers.Select(p => new RigChoiceViewModel(
                        p.ModuleId.Value,
                        p.DisplayName,
                        Strings.Current.Plural("Diagnostics.Rig.Conflict.ChoiceEvidence.Actions", p.Actions.Count)
                            + ", " + Strings.Current.Plural(
                                "Diagnostics.Rig.Conflict.ChoiceEvidence.Animations", p.StubAnimations.Count, p.LoadOrderIndex + 1),
                        p.ModuleId == conflict.AppliedLast.ModuleId,
                        DisableRigModuleCommand))],
                    conflict.AppliedLast.ModuleId.Value,
                    Folder(conflict.AppliedLast.ModuleId));

                EnableAccept(conflictRow, "RigConflict");
                RigConflictRows.Add(conflictRow);
            }

            var accounted = issues.Rig.Conflicts.SelectMany(c => c.Patchers).Select(p => p.ModuleId).ToHashSet();

            foreach (var group in issues.Rig.StubAnimations
                .Where(s => !accounted.Contains(s.ModuleId))
                .GroupBy(s => s.ModuleId))
            {
                var rigRow = new KnownIssueRowViewModel(
                    Strings.Current.Plural("Diagnostics.Rig.StubOnly.Heading", group.Count(), group.Key),
                    string.Join("; ", group.Select(s => $"{s.AnimationName} points at {s.SourceClipName} ({s.SizeBytes} bytes)")),
                    Strings.Current["Diagnostics.Rig.StubOnly.Note"],
                    group.Key.Value,
                    Folder(group.Key),
                    [
                        new KnownIssueActionViewModel(
                            Strings.Current.Format("Diagnostics.Rig.DisableLabel", group.Key),
                            Strings.Current["Diagnostics.Rig.DisableTooltip"],
                            DisableRigModuleCommand,
                            group.Key.Value)
                    ]);

                EnableAccept(rigRow, "RigIssue");
                RigIssues.Add(rigRow);
            }

            // One row per assembly name rather than one per copy. Twenty-five copies of 0Harmony were
            // twenty-five rows under the old shape and none at all under the old rule, and neither of
            // those is a report anybody reads.
            foreach (var shadowed in issues.Assemblies)
            {
                // Labeled and offered from Removable rather than from Shadowed. A copy a module's own
                // SubModule.xml declares is never moved, so on a row where every shadowed copy is
                // declared this button was showing a count it would not act on and then moving
                // nothing. A control that does nothing is worse than no control.
                List<KnownIssueActionViewModel> actions = shadowed.CanRemoveSafely
                    ?
                    [
                        new KnownIssueActionViewModel(
                            Strings.Current.Plural("Diagnostics.Rig.MoveShadowedLabel", shadowed.Removable.Count),
                            Strings.Current.Format("Diagnostics.Rig.MoveShadowedTooltip", shadowed.FileName),
                            RemoveShadowedCopiesCommand,
                            shadowed)
                    ]
                    : [];

                // The remedy the old rule offered, kept for the case it was written for. A module whose
                // copy is newer than the one that binds is the one missing API it may call, and moving
                // the earlier module below it is what makes its copy the one bound.
                //
                // Never when the earlier module is Harmony, ButterLib, UIExtenderEx or MBOptionScreen.
                // Those load before Native by requirement, and this button offered to move one of them
                // below a content mod, which is an install that does not start.
                if (shadowed.CanReorderSafely)
                {
                    foreach (var copy in shadowed.Shadowed
                        .Where(c => c.Difference == AssemblyCopyDifference.NewerThanTheOneThatBinds))
                    {
                        actions.Add(new KnownIssueActionViewModel(
                            Strings.Current.Format(
                                "Diagnostics.Rig.MoveBelowLabel", shadowed.Winner.DisplayName, copy.Copy.DisplayName),
                            Strings.Current.Format(
                                "Diagnostics.Rig.MoveBelowTooltip", copy.Copy.DisplayName, shadowed.FileName),
                            ApplyLoadOrderMoveCommand,
                            new LoadOrderMove(shadowed.Winner.ModuleId.Value, copy.Copy.ModuleId.Value)));
                    }
                }

                // A refused remedy is said out loud with its reason, on the row, rather than leaving a
                // finding with nothing under it and no explanation of why nothing is offered.
                var refusal = shadowed.CanReorderSafely
                    || !shadowed.Shadowed.Any(c => c.Difference == AssemblyCopyDifference.NewerThanTheOneThatBinds)
                        ? string.Empty
                        : " " + shadowed.DescribeReorderRefusal();

                var assemblyRow = new KnownIssueRowViewModel(
                    shadowed.Summarize(),
                    Strings.Current.Format("Diagnostics.Rig.LoadsShadowed", shadowed.Winner.Path, shadowed.DescribeCopies()),
                    shadowed.DescribeReach() + " " + Strings.Current["Diagnostics.Rig.RemovingDoesNotChangeToday"] + refusal,
                    shadowed.Winner.ModuleId.Value,
                    Folder(shadowed.Winner.ModuleId),
                    actions);

                EnableAccept(assemblyRow, "AssemblyIssue");
                AssemblyIssues.Add(assemblyRow);
            }

            foreach (var patch in issues.HarmonyPatches?.Findings ?? [])
            {
                // No button: BEM will not edit a mod's assembly, and disabling the module is a bigger
                // decision than one broken patch justifies on its own. The row opens the file holding
                // the evidence and says in one sentence what is wrong with it.
                var harmonyRow = new KnownIssueRowViewModel(
                    Strings.Current.Format("Diagnostics.Harmony.Heading", patch.ModuleName, patch.Target),
                    $"{patch.PatchClass}.{patch.PatchMethod} in {patch.AssemblyPath}. {patch.Why}",
                    Strings.Current["Diagnostics.Harmony.Note"],
                    patch.ModuleId.Value,
                    Folder(patch.ModuleId),
                    [
                        new KnownIssueActionViewModel(
                            Strings.Current["Diagnostics.OpenAssemblyLabel"],
                            Strings.Current["Diagnostics.OpenAssemblyTooltip"],
                            OpenFolderCommand,
                            Path.GetDirectoryName(patch.AssemblyPath) ?? patch.AssemblyPath),
                        new KnownIssueActionViewModel(
                            Strings.Current.Format("Diagnostics.Rig.DisableLabel", patch.ModuleName),
                            Strings.Current["Diagnostics.Rig.DisableTooltip"],
                            DisableRigModuleCommand,
                            patch.ModuleId.Value)
                    ]);

                EnableAccept(harmonyRow, "HarmonyPatchIssue");
                HarmonyPatchIssues.Add(harmonyRow);
            }

            HarmonyPatchIssuesEmptyText = HarmonyPatchIssues.Count > 0
                ? string.Empty
                : issues.HarmonyPatches?.Summary
                  ?? Strings.Current["Diagnostics.Harmony.NotInspected"];

            HarmonyPatchesUncheckedText = issues.HarmonyPatches?.DescribeUnchecked() ?? string.Empty;

            // Both folder paths in full and the timestamp of each, because that is exactly what the
            // launcher's duplicate-id error withholds: it names neither folder, so a developer whose
            // build rolled back has nothing to tell the fresh staging copy from the install that was
            // already there. Nothing here removes a folder, and nothing offers to: a half-finished
            // build belongs to the tool that made it, and only its owner knows which copy to keep.
            foreach (var collision in issues.DuplicateIds ?? [])
            {
                var folders = string.Join(
                    "\n",
                    collision.Folders.Select(folder => folder.LastWriteUtc is { } written
                        ? Strings.Current.Format(
                            "Diagnostics.DuplicateId.Folder",
                            folder.FolderPath,
                            $"{written.ToLocalTime():yyyy-MM-dd HH:mm}")
                        : Strings.Current.Format(
                            "Diagnostics.DuplicateId.FolderNoTimestamp", folder.FolderPath)));

                // No accept button. Every other install check is a standing risk the user can weigh;
                // this one is what the loader does, and muting it against the badge would not stop the
                // game refusing to start.
                DuplicateIds.Add(new KnownIssueRowViewModel(
                    Strings.Current.Format("Diagnostics.DuplicateId.Heading", collision.Id),
                    Strings.Current["Diagnostics.DuplicateId.Detail"] + "\n" + folders,
                    Strings.Current["Diagnostics.DuplicateId.Note"],
                    collision.Id.Value,
                    collision.Folders[0].FolderPath,
                    [
                        .. collision.Folders.Select(folder => new KnownIssueActionViewModel(
                            Strings.Current.Format("Diagnostics.DuplicateId.OpenLabel", folder.FolderName),
                            Strings.Current.Format("Diagnostics.DuplicateId.OpenTooltip", folder.FolderPath),
                            OpenFolderCommand,
                            folder.FolderPath))
                    ]));
            }

            foreach (var duplicate in issues.Shadowed)
            {
                var shadowedRow = new KnownIssueRowViewModel(
                    Strings.Current.Format("Diagnostics.Shadowed.Heading", duplicate.Id),
                    Strings.Current.Format(
                        "Diagnostics.Shadowed.Detail",
                        duplicate.UsedFolderPath,
                        string.Join("; ", duplicate.ShadowedFolderPaths)),
                    Strings.Current["Diagnostics.Shadowed.Note"],
                    duplicate.Id.Value,
                    duplicate.UsedFolderPath,
                    [
                        .. duplicate.ShadowedFolderPaths.Select(path => new KnownIssueActionViewModel(
                            Strings.Current["Diagnostics.Shadowed.OpenIgnoredLabel"],
                            Strings.Current.Format("Diagnostics.Shadowed.OpenIgnoredTooltip", path),
                            OpenFolderCommand,
                            path))
                    ]);

                EnableAccept(shadowedRow, "ShadowedModule");
                ShadowedModules.Add(shadowedRow);
            }

            foreach (var adapter in issues.Graphics.Adapters)
            {
                GraphicsAdapters.Add(new KnownIssueRowViewModel(
                    adapter.Name,
                    Strings.Current.Format("Diagnostics.Graphics.Driver", adapter.DriverVersion)
                      + (adapter.DriverDate is null
                          ? string.Empty
                          : Strings.Current.Format("Diagnostics.Graphics.DriverDate", $"{adapter.DriverDate:yyyy-MM-dd}")),
                    string.Empty));
            }

            // Graded and ordered the way the launch preflight already grades them. All five gaps on a
            // real install are the game being newer than the mod, which is the normal state days
            // after a patch, and listing them flat put five working mods on a known-issues page.
            foreach (var gap in issues.VersionSupport?.GapsWorstFirst ?? [])
            {
                // No button: BEM cannot compile an assembly this mod does not ship, and it will not move
                // or rename one on a guess. The row says what to do instead and opens the folder holding
                // the evidence, which is the most it can honestly offer.
                VersionGaps.Add(new KnownIssueRowViewModel(
                    $"{gap.Grade}: {gap.Headline}",
                    gap.Detail,
                    gap.Why,
                    gap.Set.ModuleId.Value,
                    Folder(gap.Set.ModuleId),
                    [
                        new KnownIssueActionViewModel(
                            Strings.Current["Diagnostics.VersionGaps.OpenAssembliesLabel"],
                            Strings.Current["Diagnostics.VersionGaps.OpenAssembliesTooltip"],
                            OpenFolderCommand,
                            gap.Set.FolderPath)
                    ]));
            }

            VersionGapsEmptyText = VersionGaps.Count > 0
                ? string.Empty
                : issues.VersionSupport?.Summary
                  ?? Strings.Current["Diagnostics.VersionGaps.NotInspected"];

            VersionGapsGradingText = issues.VersionSupport?.GapGrading ?? string.Empty;

            ShowMergeFaults(issues, Folder);
            ShowXmlOverlaps(issues, Folder);

            RigIssuesEmptyText = RigIssues.Count + RigConflictRows.Count > 0
                ? string.Empty
                : Strings.Current["Diagnostics.Rig.Empty"];

            var orderFix = issues.Rig.OrderFix;

            RigOrderStatusText = orderFix is null ? string.Empty : Describe(orderFix);
            CanApplyRigOrderFix = orderFix?.IsNeeded ?? false;

            // The safe half of a rig conflict, applied rather than offered. Keeping Native above every
            // module that grafts onto its action sets is not a choice between two mods: CreateMergedXmlFile
            // never applies xsltList[0], so anything sorted above Native is silently dropped. There is one
            // correct order and BEM knows it, so waiting to be asked only leaves the setup broken longer.
            //
            // Which of two conflicting mods to disable is the other half and stays the user's, listed
            // below with a button each. Applying this is undoable and is written like any other change.
            if (CanApplyRigOrderFix)
                ApplyNativeFirstAutomatically(orderFix!);

            GameAssemblyCopies.Clear();

            if (issues.GameShadows is { } shadows)
            {
                foreach (var module in shadows.ByModule)
                {
                    var differing = module.Count(shadow => !shadow.Identical);

                    var copyRow = new KnownIssueRowViewModel(
                        differing > 0
                            ? Strings.Current.Plural("Diagnostics.GameAssemblyCopy.Heading.Differing", differing, module.First().DisplayName)
                            : Strings.Current.Plural("Diagnostics.GameAssemblyCopy.Heading.Same", module.Count(), module.First().DisplayName),
                        shadows.Describe(module),
                        differing > 0
                            ? Strings.Current["Diagnostics.GameAssemblyCopy.CannotFix"]
                            : string.Empty,
                        module.Key.Value,
                        Folder(module.Key));

                    EnableAccept(copyRow, "GameAssemblyCopy");
                    GameAssemblyCopies.Add(copyRow);
                }
            }

            GameAssemblyCopiesEmptyText = GameAssemblyCopies.Count > 0
                ? string.Empty
                : Strings.Current["Diagnostics.GameAssemblyCopy.Empty"];

            // Counted from the rows themselves rather than from the reports, so the badge can never
            // disagree with what opening the page shows. An accepted row stays in its list - it is
            // still true - but stops counting against the badge, same as a preflight risk once accepted.
            RecountInstallChecks();

            AssemblyIssuesEmptyText = AssemblyIssues.Count > 0
                ? string.Empty
                : Strings.Current["Diagnostics.AssemblyIssues.Empty"];

            ShadowedModulesEmptyText = ShadowedModules.Count > 0
                ? string.Empty
                : Strings.Current["Diagnostics.Shadowed.Empty"];

            DuplicateIdsEmptyText = DuplicateIds.Count > 0
                ? string.Empty
                : Strings.Current["Diagnostics.DuplicateId.Empty"];

            GraphicsAdaptersEmptyText = issues.Graphics.Error
                ?? (GraphicsAdapters.Count > 0 ? string.Empty : Strings.Current["Diagnostics.Graphics.Empty"]);

            KnownIssuesStatus = issues.ScanError is null
                ? Strings.Current.Format("Diagnostics.KnownIssues.ReadFrom", GameInstallPath)
                : Strings.Current.Format("Diagnostics.KnownIssues.CouldNotRun", issues.ScanError);
        }

        private void RecountInstallChecks()
        {
            InstallCheckFindingCount =
                RigIssues.Count(r => !r.Accepted)
                + RigConflictRows.Count(r => !r.Accepted)
                + AssemblyIssues.Count(r => !r.Accepted)
                + ShadowedModules.Count(r => !r.Accepted)
                + DuplicateIds.Count
                + HarmonyPatchIssues.Count(r => !r.Accepted)
                + GameAssemblyCopies.Count(r => !r.Accepted);

            AcceptedInstallCheckCount =
                RigIssues.Count(r => r.Accepted)
                + RigConflictRows.Count(r => r.Accepted)
                + AssemblyIssues.Count(r => r.Accepted)
                + ShadowedModules.Count(r => r.Accepted)
                + HarmonyPatchIssues.Count(r => r.Accepted)
                + GameAssemblyCopies.Count(r => r.Accepted);
        }

        // The user reading an install-check or boot-check finding and choosing to stop counting it
        // against the Health badge. Recorded by category and headline and re-matched on the next Show
        // or ShowDryRun; the row is marked here immediately too, so the badge and the button both agree
        // without waiting for a full rescan that can take seconds.
        private void AcceptFinding(KnownIssueRowViewModel row)
        {
            if (row.Category is not { } category)
                return;

            var record = AcceptedFindings.Accept([new AcceptedFinding(category, row.Heading, DateTimeOffset.UtcNow)]);

            row.MarkAccepted();
            RecountInstallChecks();
            AcceptedBootCheckCount = DryRunFindings.Count(r => r.Accepted);
            KnownIssuesStatus = record.Describe();
            RefreshAcceptedFindingRows();
        }

        public ObservableCollection<AcceptedFindingRowViewModel> AcceptedFindingRows { get; } = [];

        [ObservableProperty]
        public partial bool HasAcceptedFindings { get; set; }

        private void ForgetAcceptedFinding(AcceptedFindingRowViewModel row)
        {
            var record = AcceptedFindings.Forget([row.Finding.Key]);

            KnownIssuesStatus = record.Describe();
            RefreshAcceptedFindingRows();

            // A forgotten finding is read again on the next check, not retroactively: re-marking every
            // row on screen would mean re-running checks that already finished, for a state change that
            // only matters the next time those checks run anyway.
        }

        internal void RefreshAcceptedFindingRows()
        {
            AcceptedFindingRows.Clear();

            foreach (var finding in AcceptedFindings.Load())
                AcceptedFindingRows.Add(new AcceptedFindingRowViewModel(finding, ForgetAcceptedFinding));

            HasAcceptedFindings = AcceptedFindingRows.Count > 0;
        }

        // "Found nothing" and "could not look" are different statements, and a sweep that answered
        // 2000 questions and refused 200 has to say so or it reads as a clean bill of health.
        // The merge the game runs when a campaign starts, run here to see whether it finishes. Every row
        // is a place MBObjectManager throws, so a working install has none of them and one row is a
        // crash rather than a warning. The module, the file, the element path and the schema are all in
        // the row because every one of them has to be quotable to the mod's author.
        private void ShowMergeFaults(KnownIssues issues, Func<ModuleId, string> folder)
        {
            if (issues.MergeFaults is not { } report)
            {
                MergeFaultsSummary = issues.ScanError is null
                    ? Strings.Current["Diagnostics.MergeFaults.NotRun"]
                    : Strings.Current.Format("Diagnostics.MergeFaults.CouldNotRun", issues.ScanError);
                MergeFaultsEmptyText = string.Empty;
                return;
            }

            foreach (var fault in report.Faults)
            {
                var culprit = fault.Sources.Count > 0 ? fault.Sources[0] : fault.Trigger;
                // Five separate strings the user will want to paste somewhere, one per line so each can
                // be selected on its own rather than out of a paragraph.
                var evidence = fault.Kind == XmlMergeFaultKind.UndeclaredElementPath
                    ? string.Join(Environment.NewLine,
                        culprit.Path,
                        Strings.Current.Format("Diagnostics.MergeFaults.Evidence.ElementPath", fault.ElementPath),
                        Strings.Current.Format("Diagnostics.MergeFaults.Evidence.Entry", fault.EntryPath),
                        Strings.Current.Format("Diagnostics.MergeFaults.Evidence.Schema", fault.SchemaPath),
                        Strings.Current.Format("Diagnostics.MergeFaults.Evidence.ReachedBy", fault.Trigger.Path))
                    : culprit.Path;

                var actions = new List<KnownIssueActionViewModel>
                {
                    new(
                        Strings.Current["Diagnostics.OpenFileLabel"],
                        Strings.Current["Diagnostics.OpenFileTooltip"],
                        OpenFolderCommand,
                        Path.GetDirectoryName(culprit.Path) ?? culprit.Path),
                    new(
                        Strings.Current.Format("Diagnostics.Rig.DisableLabel", culprit.DisplayName),
                        Strings.Current["Diagnostics.Rig.DisableTooltip"],
                        DisableRigModuleCommand,
                        culprit.ModuleId.Value)
                };

                // Either module ends the crash, and which one the user would rather keep is their call
                // and not BEM's. Offering only the one that shipped the bad element would decide it for them.
                if (fault.Trigger.ModuleId != culprit.ModuleId)
                {
                    actions.Add(new KnownIssueActionViewModel(
                        Strings.Current.Format("Diagnostics.Rig.DisableLabel", fault.Trigger.DisplayName),
                        Strings.Current["Diagnostics.MergeFaults.DisableOtherHalfTooltip"],
                        DisableRigModuleCommand,
                        fault.Trigger.ModuleId.Value));
                }

                MergeFaults.Add(new KnownIssueRowViewModel(
                    MergeFaultHeadline(fault),
                    evidence,
                    fault.Describe(),
                    culprit.ModuleId.Value,
                    folder(culprit.ModuleId),
                    actions));
            }

            MergeFaultsSummary = Strings.Current.Plural("Diagnostics.MergeFaults.Summary.Datasets", report.DatasetsChecked)
                + " " + Strings.Current.Plural(
                    "Diagnostics.MergeFaults.Summary.Files", report.FilesMerged, issues.MergeFaultMilliseconds)
                + " " + Strings.Current.Plural("Diagnostics.MergeFaults.Summary.Stop", report.Faults.Count);

            MergeFaultsEmptyText = MergeFaults.Count > 0
                ? string.Empty
                : Strings.Current["Diagnostics.MergeFaults.Empty"];
        }

        private static string MergeFaultHeadline(XmlMergeFault fault) => fault.Kind switch
        {
            XmlMergeFaultKind.UndeclaredElementPath =>
                Strings.Current.Format("Diagnostics.MergeFaults.Headline.Undeclared", fault.Dataset, fault.ElementPath),
            XmlMergeFaultKind.SchemaNeverRead =>
                Strings.Current.Format("Diagnostics.MergeFaults.Headline.SchemaNeverRead", fault.Dataset, fault.Trigger.DisplayName),
            _ => Strings.Current.Format("Diagnostics.MergeFaults.Headline.NotShipped", fault.Dataset, fault.Trigger.DisplayName)
        };

        private void ShowXmlOverlaps(KnownIssues issues, Func<ModuleId, string> folder)
        {
            if (issues.XmlOverlaps is not { } report)
            {
                XmlOverlapSummary = issues.ScanError is null
                    ? Strings.Current["Diagnostics.XmlOverlap.NotInspected"]
                    : Strings.Current.Format("Diagnostics.XmlOverlap.CouldNotInspect", issues.ScanError);
                XmlOverlapConflictsEmptyText = string.Empty;
                XmlOverlapOverridesEmptyText = string.Empty;
                XmlDatasetProblemsEmptyText = string.Empty;
                XmlDatasetsLoadedElsewhereText = string.Empty;
                XmlTransformsEmptyText = string.Empty;
                XmlComposedText = string.Empty;
                XmlTransformsUncertainText = string.Empty;
                return;
            }

            foreach (var group in report.Groups)
            {
                var row = Describe(group, folder);

                if (group.Grade == XmlOverlapGrade.NativeOverride)
                    XmlOverlapOverrides.Add(row);
                else
                    XmlOverlapConflicts.Add(row);
            }

            foreach (var problem in report.Problems)
            {
                // Three different situations used to share one headline, and only the first of them was
                // ever about BEM. Saying "BEM read no entries from it" about a mod that ships nothing put
                // the fault in the wrong place, and about a mod that ships its file under GUI it was
                // simply untrue.
                var headline = problem.Kind switch
                {
                    XmlDatasetProblemKind.NotShipped =>
                        Strings.Current.Format("Diagnostics.XmlOverlap.Problem.NotShipped", problem.DisplayName, problem.Dataset),
                    XmlDatasetProblemKind.LoadedElsewhere =>
                        Strings.Current.Format("Diagnostics.XmlOverlap.Problem.LoadedElsewhere", problem.DisplayName, problem.Dataset),
                    _ => Strings.Current.Format("Diagnostics.XmlOverlap.Problem.CouldNotRead", problem.Dataset, problem.DisplayName)
                };

                var row = new KnownIssueRowViewModel(
                    headline,
                    problem.Path,
                    problem.Reason,
                    problem.ModuleId.Value,
                    folder(problem.ModuleId));

                if (problem.Kind == XmlDatasetProblemKind.LoadedElsewhere)
                    XmlDatasetsLoadedElsewhere.Add(row);
                else
                    XmlDatasetProblems.Add(row);
            }

            // Kept for the launch preflight, which cannot afford to run the stylesheets itself. This scan
            // is the only place that cost is already being paid.
            XmlTransformCache.Store(XmlTransformCache.SignatureFor(EnabledModules()), report.Transforms);

            HasXmlTransforms = report.Transforms.Count > 0;
            HasBaseGameOverrides = report.Groups.Any(group => group.Grade == XmlOverlapGrade.NativeOverride);

            // A stylesheet that empties a dataset outranks every ordinary edit in the list: it is the one
            // that stops the game reading anything at all, and buried among "3 added, 2 replaced" rows it
            // reads like more of the same.
            foreach (var transform in report.Transforms.OrderByDescending(t => t.EmptiesDataset))
            {
                XmlTransforms.Add(new KnownIssueRowViewModel(
                    transform.Describe(),
                    transform.Path,
                    string.Empty,
                    transform.ModuleId.Value,
                    folder(transform.ModuleId)));
            }

            XmlOverlapSummary =
                Strings.Current.Plural("Diagnostics.XmlOverlap.Summary.Entries", report.EntriesIndexed)
                + " " + Strings.Current.Plural("Diagnostics.XmlOverlap.Summary.Files", report.FilesParsed)
                + " " + Strings.Current.Plural(
                    "Diagnostics.XmlOverlap.Summary.Modules", report.ModulesWithDatasets, issues.XmlOverlapMilliseconds)
                + " " + Strings.Current.Plural(
                    "Diagnostics.XmlOverlap.Summary.Overlaps", report.RawOverlapCount, report.OfficialOnlyCount)
                + " " + Strings.Current.Plural(
                    "Diagnostics.XmlOverlap.Summary.Contested", report.ContestedValues, report.Groups.Count);

            // The reassuring number, said as a result and never as a backlog. Most of an install's
            // overlaps land here, and a reader who mistook this for a problem count would think a clean
            // install was the worst one they had ever seen.
            XmlComposedText = report.ComposedOverlapCount > 0
                ? Strings.Current.Plural("Diagnostics.XmlOverlap.Composed", report.ComposedOverlapCount)
                : string.Empty;

            XmlOverlapConflictsEmptyText = XmlOverlapConflicts.Count > 0
                ? string.Empty
                : Strings.Current["Diagnostics.XmlOverlap.Conflicts.Empty"];

            XmlOverlapOverridesEmptyText = XmlOverlapOverrides.Count > 0
                ? string.Empty
                : Strings.Current["Diagnostics.XmlOverlap.Overrides.Empty"];

            XmlDatasetProblemsEmptyText = XmlDatasetProblems.Count > 0
                ? string.Empty
                : Strings.Current["Diagnostics.XmlOverlap.Problems.Empty"];

            XmlDatasetsLoadedElsewhereText = XmlDatasetsLoadedElsewhere.Count > 0
                ? Strings.Current.Plural("Diagnostics.XmlOverlap.LoadedElsewhere", XmlDatasetsLoadedElsewhere.Count)
                : string.Empty;

            var refused = report.Transforms.Count(t => t.Error is not null);
            var skipped = report.Transforms.Count(t => !t.Applied);
            var overriding = report.Transforms.Count(t => t.Overrode > 0);

            XmlTransformsEmptyText = report.Transforms.Count > 0
                ? string.Empty
                : Strings.Current["Diagnostics.XmlOverlap.Transforms.Empty"];

            // BEM applies each stylesheet at the point in the pipeline the engine applies it, to the
            // document the engine would be holding, so what it changed is stated rather than guessed at.
            // Where it rewrites an entry an earlier module defined, it simply runs later and wins.
            XmlTransformsUncertainText = overriding > 0
                ? Strings.Current.Plural("Diagnostics.XmlOverlap.Transforms.Overriding", report.Transforms.Count, overriding)
                : string.Empty;

            if (skipped > 0)
            {
                XmlTransformsUncertainText += (XmlTransformsUncertainText.Length > 0 ? " " : string.Empty)
                    + Strings.Current.Plural("Diagnostics.XmlOverlap.Transforms.Skipped", skipped);
            }

            if (refused > 0)
            {
                XmlOverlapSummary += " " + Strings.Current.Plural(
                    "Diagnostics.XmlOverlap.Transforms.Refused", report.Transforms.Count, refused);
            }
        }

        private XmlOverlapRowViewModel Describe(XmlOverlapGroup group, Func<ModuleId, string> folder)
        {
            var grade = group.Grade switch
            {
                XmlOverlapGrade.NativeOverride => Strings.Current["Diagnostics.XmlOverlap.Grade.BaseGameData"],
                XmlOverlapGrade.DeclaredLayering => Strings.Current["Diagnostics.XmlOverlap.Grade.OrderDeclared"],
                _ => Strings.Current["Diagnostics.XmlOverlap.Grade.OrderUndeclared"]
            };

            var outcome = group.Grade == XmlOverlapGrade.NativeOverride
                ? Strings.Current.Format("Diagnostics.XmlOverlap.Outcome.ReplacesBaseGame", group.Winner.DisplayName)
                : group.Definers.Count == 2
                    ? Strings.Current.Format(
                        "Diagnostics.XmlOverlap.Outcome.WinsOverOne", group.Winner.DisplayName, group.Definers[0].DisplayName)
                    : Strings.Current.Plural(
                        "Diagnostics.XmlOverlap.Outcome.WinsOverMany", group.Definers.Count - 1, group.Winner.DisplayName);

            // The finding is the contested attribute, so the element path and attribute lead the row.
            // "Items, 153 entry(s)" is true of nearly every content mod and tells the user nothing;
            // "/Items/Item@weight" is the thing being argued over.
            var target = group.ElementPath.Length == 0
                ? group.Dataset
                : group.ElementPath
                  + (group.Attribute.Length > 0 ? "@" + group.Attribute : " " + Strings.Current["Diagnostics.XmlOverlap.ElementText"]);

            // Ordered by entry so a row carrying hundreds of them reads down the column, rather than in
            // whatever order the merge happened to record them.
            var contests = group.Contests
                .OrderBy(c => c.EntryId, StringComparer.Ordinal)
                .Select(c => c.Describe())
                .ToList();

            // Only a mod can be moved. Sorting an official module below the mods that override it is
            // not a remedy, it is a different fault, so those never get a button. One button covers every
            // contest in the row, because the row is one attribute and the move settles all of them.
            //
            // Harmony, ButterLib, UIExtenderEx and MBOptionScreen are excluded for the same reason as an
            // official module and not a weaker one: they load before Native by requirement, so moving one
            // down to win an XML contest is an install that does not start. The shadowed assembly remedy
            // shipped exactly that mistake, and this is the same button with a different subject.
            var challengers = group.Definers
                .Where(d => !d.IsOfficial
                    && d.ModuleId != group.Winner.ModuleId
                    && !ModuleTiers.LoadsBeforeContent(d.ModuleId))
                .Select(d => new KnownIssueActionViewModel(
                    Strings.Current.Format("Diagnostics.XmlOverlap.MakeWinLabel", d.DisplayName),
                    Strings.Current.Format(
                        "Diagnostics.XmlOverlap.MakeWinTooltip", d.DisplayName, group.Winner.DisplayName),
                    ApplyLoadOrderMoveCommand,
                    new LoadOrderMove(d.ModuleId.Value, group.Winner.ModuleId.Value)))
                .ToList();

            return new XmlOverlapRowViewModel(
                Strings.Current.Plural("Diagnostics.XmlOverlap.Row.Headline", group.EntryIds.Count, target),
                outcome,
                Strings.Current.Format("Diagnostics.XmlOverlap.Row.Grade", group.Dataset, grade),
                Strings.Current.Format("Diagnostics.XmlOverlap.Row.ContestedPath", target, group.Dataset),
                string.Join(" > ", group.Definers.Select(d => d.DisplayName)),
                contests.Count > 0 ? Strings.Current.Plural("Diagnostics.XmlOverlap.Row.ContestsCaption", contests.Count) : string.Empty,
                contests,
                Strings.Current.Plural("Diagnostics.XmlOverlap.Row.IdsCaption", group.EntryIds.Count),
                string.Join(", ", group.EntryIds),
                challengers,
                group.Winner.ModuleId.Value,
                folder(group.Winner.ModuleId));
        }

        // BEM's own copies of what the game wrote about a crash and then deleted. They are listed
        // separately from the reports themselves because their whole point is that the originals are
        // gone, so nothing here ever removes one on its own: every removal is one the user named and
        // answered for, and every one of them goes to the Recycle Bin.
        public ObservableCollection<KnownIssueRowViewModel> Captures { get; } = [];

        [ObservableProperty]
        public partial string CapturesSummary { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string RetentionSummary { get; set; } = string.Empty;

        // Doubles, because that is what the boxes hand back and an empty box hands back NaN. Reading
        // that as an int is what once wrote "keep 1" from a single backspace, so the number never
        // becomes an int until the settings have accepted it.
        [ObservableProperty]
        public partial double KeepCaptures { get; set; } = ArtifactCaptureRetention.DefaultKeep;

        [ObservableProperty]
        public partial double KeepDumps { get; set; } = ArtifactCaptureRetention.DefaultKeepDumps;

        partial void OnKeepCapturesChanged(double value) => SaveRetention();

        partial void OnKeepDumpsChanged(double value) => SaveRetention();

        private void SaveRetention()
        {
            if (readingRetention)
                return;

            // Nothing was stored, so the limits in force are still the ones to show, and writing them
            // back is what puts the figure into the box the user emptied.
            if (!retentionSettings.Set(KeepCaptures, KeepDumps))
                ReadRetention();

            RetentionSummary = ArtifactCaptureRetention.Describe(
                CaptureRoot, retentionSettings.Keep, retentionSettings.KeepDumps);
        }

        private void ReadRetention()
        {
            readingRetention = true;

            try
            {
                KeepCaptures = retentionSettings.Keep;
                KeepDumps = retentionSettings.KeepDumps;
            }
            finally
            {
                readingRetention = false;
            }
        }

        private static string CaptureRoot => CrashArtifactPaths.GetDefaultRoot(ResolveDataRoot());

        // Ages the store on demand rather than waiting for the next capture to do it. It hands in the
        // Recycle Bin, unlike the sweep at the end of a capture, because a prune the user pressed is one
        // they may want back and the disk space is theirs to reclaim by emptying the bin.
        [RelayCommand(CanExecute = nameof(CanScan))]
        private async Task PruneCapturesAsync()
        {
            var files = ArtifactCaptureStore.StoredFiles(CaptureRoot);

            if (files.Count == 0)
            {
                StatusMessage = Strings.Current.Format("Diagnostics.Captures.NothingToPrune", CaptureRoot);
                return;
            }

            // The stored limits rather than the boxes: what the prune acts on has to be what was
            // accepted and applied, never a figure sitting unread in a control.
            var keep = retentionSettings.Keep;
            var keepDumps = retentionSettings.KeepDumps;

            if (!await ConfirmAsync(
                    Strings.Current.Format("Diagnostics.Captures.PruneConfirm.Title", keep),
                    Strings.Current.Format("Diagnostics.Captures.PruneConfirm.Body", keep, keepDumps),
                    Strings.Current["Diagnostics.Captures.PruneConfirm.PrimaryButton"]))
            {
                return;
            }

            IsBusy = true;

            ArtifactCapturePrune prune;

            try
            {
                prune = await Task.Run(() => ArtifactCaptureRetention.Prune(
                    CaptureRoot, keep, keepDumps, RecycleFolder, RecycleFile));
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to prune the captures");
                StatusMessage = Strings.Current.Format("Diagnostics.Captures.NothingPruned", ex.Message);
                return;
            }
            finally
            {
                IsBusy = false;
            }

            ShowCaptures();

            StatusMessage = prune.RemovedAnything
                ? Strings.Current.Format("Diagnostics.Clear.InRecycleBin", prune.Describe(keep, keepDumps))
                : prune.Describe(keep, keepDumps);
        }

        // The other half of a list that can only grow. A capture is evidence that exists nowhere else, so
        // the question names the file count and the size, and the folder goes to the Recycle Bin.
        [RelayCommand]
        private async Task RemoveCaptureAsync(string? folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return;

            var scope = ArtifactCaptureStore.MeasureCapture(folderPath);

            if (!await ConfirmAsync(
                    Strings.Current.Format(
                        "Diagnostics.Captures.RemoveConfirm.Title", scope.Describe("Diagnostics.Clear.CountedNoun.CapturedFile")),
                    Strings.Current.Format("Diagnostics.Captures.RemoveConfirm.Body", folderPath),
                    Strings.Current["Diagnostics.Captures.RemoveConfirm.PrimaryButton"]))
            {
                return;
            }

            IsBusy = true;

            CaptureRemoval removal;

            try
            {
                removal = await Task.Run(() => ArtifactCaptureStore.Remove(CaptureRoot, folderPath, RecycleFolder));
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to remove a capture");
                StatusMessage = Strings.Current.Format("Diagnostics.Clear.NothingRemoved", ex.Message);
                return;
            }
            finally
            {
                IsBusy = false;
            }

            ShowCaptures();

            StatusMessage = removal.Removed
                ? Strings.Current.Format("Diagnostics.Captures.RemovedIsInRecycleBin", removal.Describe())
                : removal.Describe();
        }

        private async Task ShowCrashDumpsAsync()
        {
            var folder = Core.Diagnostics.CrashDumps.GetDefaultFolder();

            // BEM's own dumps sit in the same folder as the game's and are nobody's mod. They are
            // listed rather than hidden, marked as BEM's, and removable like everything else.
            var own = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

            var search = await Task.Run(() => Core.Diagnostics.CrashDumps.Find(
                folder,
                name => CrashArtifactPaths.IsWatchedProcess(name)
                    || string.Equals(name, own, StringComparison.OrdinalIgnoreCase)));

            CrashDumpRows.Clear();

            foreach (var dump in search.Dumps)
            {
                CrashDumpRows.Add(new CrashDumpRowViewModel(
                    dump,
                    string.Equals(dump.ProcessName, own, StringComparison.OrdinalIgnoreCase),
                    RemoveCrashDumpCommand));
            }

            lastDumps = search.Dumps;

            // Said by the search itself, because it is the only thing that knows whether an empty list
            // of dumps is a folder holding none or a folder that refused to be read.
            CrashDumpsStatus = search.Describe(folder);

            var settings = await Task.Run(LocalDumps.Read);

            DumpSettingsText = settings.Describe();
            CanRestoreDumpSettings = File.Exists(LocalDumps.GetRestoreScriptPath());
        }

        // The Windows Application event log, read for the processes BEM knows it can launch and for BEM
        // itself. This is the source that outlives a sweep: when every crash folder on the machine has
        // been cleared, Windows still knows the game died and how, and it is the only witness to a run
        // that wrote no log at all.
        //
        // It reads. Nothing here writes an event, clears a log or asks for elevation, and it runs only
        // when the user presses Scan, so nothing about it touches startup.
        private async Task ShowWindowsFaultsAsync()
        {
            var own = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

            // BEM's own faults sit in the same log as the game's and are nobody's mod. They are listed
            // rather than hidden and marked as BEM's, the same way its own dumps are.
            var reading = await Task.Run(() => WindowsFaultLog.Read(
                executable => WindowsFaultLog.IsGameProcess(executable)
                    || string.Equals(Path.GetFileNameWithoutExtension(executable), own, StringComparison.OrdinalIgnoreCase)));

            var captures = await Task.Run(() => ArtifactCaptureStore.List(CaptureRoot));

            // The fourth view. The game's own crash folder carries the process id in the name of the
            // engine log inside it, which is the same id the event log and the dump's file name carry.
            var crashFolders = await Task.Run(() => GameCrashFolders.Find(GameInstallPath, ResolveDataRoot()).Folders);

            WindowsFaultRows.Clear();

            foreach (var group in reading.Groups)
            {
                WindowsFaultRows.Add(new WindowsFaultRowViewModel(
                    group,
                    FaultCorrelation.JoinAll(group.Records, lastDumps, lastLogs, captures, crashFolders),
                    string.Equals(group.Newest.ProcessStem, own, StringComparison.OrdinalIgnoreCase),
                    CopyWindowsFaultCommand));
            }

            WindowsFaultsStatus = reading.Describe();
        }

        [RelayCommand]
        private void CopyGameCrashFolder(GameCrashFolderRowViewModel? row)
        {
            if (row is null)
            {
                StatusMessage = Strings.Current["Diagnostics.Copy.Nothing"];
                return;
            }

            Copy(row.Text, Strings.Current["Diagnostics.Copy.What.GameCrashFolder"]);
        }

        [RelayCommand]
        private void OpenGameCrashFolder(GameCrashFolderRowViewModel? row)
        {
            if (row is null)
                return;

            Start(
                new System.Diagnostics.ProcessStartInfo(row.Path) { UseShellExecute = true },
                Strings.Current.Format("Diagnostics.Opened", row.Path));
        }

        [RelayCommand]
        private void CopyWindowsFault(WindowsFaultRowViewModel? row)
        {
            if (row is null)
            {
                StatusMessage = Strings.Current["Diagnostics.Copy.Nothing"];
                return;
            }

            Copy(row.Text, Strings.Current["Diagnostics.Copy.What.WindowsFault"]);
        }

        // The only open there is for an event log record. Windows has no way to be pointed at one
        // record from outside, so this opens the viewer and the row above says which record to look
        // for. BEM offers no way to clear an event log: that is destructive, machine-wide, needs
        // administrator rights, and would destroy the one crash record a sweep cannot reach.
        [RelayCommand]
        private void OpenEventViewer() => Start(
            new System.Diagnostics.ProcessStartInfo("eventvwr.msc") { UseShellExecute = true },
            Strings.Current["Diagnostics.Dumps.OpenedEventViewer"]);

#if DEV_BEM
        // The outer ring, both directions together. Turning full dumps on writes Windows' own registry
        // keys machine-wide and asks for administrator rights: that is a change outside BEM, made to
        // read somebody else's compiled code back, and a player is served by what Windows already
        // keeps. Every build still reads the setting and says what it costs.
        //
        // Turning full dumps on is a change outside BEM's own state and needs administrator rights, so
        // it is never a default and never silent: the exact script is on screen, the restore script is
        // written first so the reverse direction exists before the forward one runs, and the elevation
        // prompt is Windows' own.
        [RelayCommand]
        private async Task ConfigureFullDumpsAsync()
        {
            var before = LocalDumps.Read();

            if (before.Error is not null)
            {
                StatusMessage = before.Describe();
                return;
            }

            var plan = LocalDumps.PlanFullDumps(before, dumpCount: 2);

            if (plan.RegistryPaths.Count == 0)
            {
                StatusMessage = Strings.Current["Diagnostics.Dumps.AlreadyFull"];
                return;
            }

            if (!await ConfirmAsync(
                    plan.Title,
                    Strings.Current.Format("Diagnostics.Dumps.WholeChange", plan.Note, plan.Script),
                    Strings.Current["Diagnostics.Dumps.AskForRights"]))
            {
                return;
            }

            var restore = LocalDumps.PlanRestore(before);

            try
            {
                var restorePath = LocalDumps.GetRestoreScriptPath();
                Directory.CreateDirectory(Path.GetDirectoryName(restorePath)!);
                await File.WriteAllTextAsync(restorePath, restore.Script);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusMessage = Strings.Current.Format("Diagnostics.Dumps.CouldNotWriteRestoreScript", ex.Message);
                return;
            }

            StatusMessage = Import(plan)
                ? Strings.Current.Format("Diagnostics.Dumps.FullDumpsAccepted", plan.Title)
                : StatusMessage;

            CanRestoreDumpSettings = File.Exists(LocalDumps.GetRestoreScriptPath());
            DumpSettingsText = LocalDumps.Read().Describe();
        }

        [RelayCommand(CanExecute = nameof(CanRestoreDumpSettings))]
        private async Task RestoreDumpSettingsAsync()
        {
            var path = LocalDumps.GetRestoreScriptPath();

            if (!File.Exists(path))
            {
                StatusMessage = Strings.Current.Format("Diagnostics.Dumps.NoRestoreScript", path);
                return;
            }

            string script;

            try
            {
                script = await File.ReadAllTextAsync(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusMessage = Strings.Current.Format("Diagnostics.Dumps.CouldNotReadRestoreScript", path, ex.Message);
                return;
            }

            if (!await ConfirmAsync(
                    Strings.Current["Diagnostics.Dumps.RestoreConfirm.Title"],
                    Strings.Current.Format(
                        "Diagnostics.Dumps.WholeChange", Strings.Current["Diagnostics.Dumps.RestoreConfirm.Body"], script),
                    Strings.Current["Diagnostics.Dumps.AskForRights"]))
            {
                return;
            }

            if (Import(new LocalDumpPlan(
                Strings.Current["Diagnostics.Dumps.RestoreConfirm.Title"], script, [], string.Empty)))
            {
                StatusMessage = Strings.Current["Diagnostics.Dumps.RestoreAccepted"];
            }

            DumpSettingsText = LocalDumps.Read().Describe();
        }

        // Windows' own registry importer, run elevated through Windows' own prompt. BEM writes no
        // registry key itself, so a refused prompt leaves the machine exactly as it was and says so.
        private bool Import(LocalDumpPlan plan)
        {
            string scriptPath;

            try
            {
                scriptPath = Path.Combine(Path.GetTempPath(), $"bem-local-dumps-{Guid.NewGuid():N}.reg");
                File.WriteAllText(scriptPath, plan.Script);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusMessage = Strings.Current.Format("Diagnostics.Dumps.Import.CouldNotWriteScript", ex.Message);
                return false;
            }

            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "reg.exe",
                    Arguments = $"import \"{scriptPath}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                });

                if (process is null)
                {
                    StatusMessage = Strings.Current["Diagnostics.Dumps.Import.NoRegistryEditor"];
                    return false;
                }

                process.WaitForExit();

                if (process.ExitCode == 0)
                    return true;

                StatusMessage = Strings.Current.Format("Diagnostics.Dumps.Import.Refused", process.ExitCode, scriptPath);

                return false;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                StatusMessage = Strings.Current.Format("Diagnostics.Dumps.Import.RightsRefused", scriptPath, ex.Message);

                return false;
            }
        }
#endif

        // Listed means removable. A dump is tens or hundreds of megabytes and BEM's own publish smoke
        // test leaves one behind every release, so the folder only ever grows unless somebody can act
        // on it. It goes to the Recycle Bin, and BEM removes nothing from that folder on its own.
        [RelayCommand]
        private async Task RemoveCrashDumpAsync(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (!await ConfirmAsync(
                    Strings.Current["Diagnostics.Dumps.RemoveConfirm.Title"],
                    Strings.Current.Format("Diagnostics.Dumps.RemoveConfirm.Body", path),
                    Strings.Current["Diagnostics.MoveToRecycleBinButton"]))
            {
                return;
            }

            try
            {
                RecycleFile(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusMessage = Strings.Current.Format("Diagnostics.Clear.NothingMoved", ex.Message);
                return;
            }

            StatusMessage = File.Exists(path)
                ? Strings.Current.Format("Diagnostics.NoLongerOnDisk.NothingMoved", path)
                : Strings.Current.Format("Diagnostics.MovedToRecycleBin", path);

            await ShowCrashDumpsAsync();
        }

        // The other half of naming the problem. Every file it would touch is on screen before anything
        // moves, the copy the game loads is never one of them, and the Recycle Bin is where they go so
        // a mod that turns out to need its own bundled build can have it straight back.
        [RelayCommand]
        private async Task RemoveShadowedCopiesAsync(ShadowedAssembly? finding)
        {
            if (finding is null || finding.Shadowed.Count == 0)
                return;

            if (!await ConfirmAsync(
                    Strings.Current.Plural("Diagnostics.Rig.RemoveShadowedConfirm.Title", finding.Shadowed.Count, finding.FileName),
                    Strings.Current.Format(
                        "Diagnostics.Rig.RemoveShadowedConfirm.Body", finding.Winner.Path, finding.DescribePaths()),
                    Strings.Current["Diagnostics.MoveToRecycleBinButton"]))
            {
                return;
            }

            IsBusy = true;

            ShadowedCopyRemoval removal;

            try
            {
                removal = await Task.Run(() => DuplicateAssemblies.RemoveShadowedCopies(finding, RecycleFile));
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to remove the shadowed assembly copies");
                StatusMessage = Strings.Current.Format("Diagnostics.Clear.NothingMoved", ex.Message);
                return;
            }
            finally
            {
                IsBusy = false;
            }

            StatusMessage = removal.Describe();

            if (removal.Removed.Count == 0)
                return;

            // The files are gone from disk, so the index that still lists them is wrong. Re-checking
            // without this re-read the old index and put the same rows back, which made a removal that
            // had worked look like a button that does nothing.
            InvalidateIndex();

            await CheckInstallAsync();
        }

        private static void RecycleFolder(string path) =>
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

        private static void RecycleFile(string path) =>
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

        private static async Task<bool> ConfirmAsync(string title, string body, string primary)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = primary,
                CloseButtonText = Strings.Current["Diagnostics.CancelButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }

        // Reading the capture store costs a directory walk and no game folder, so the page that manages
        // these files fills itself on open rather than making the user run a full scan to see them.
        [RelayCommand]
        private void RefreshCaptures() => ShowCaptures();

        private void ShowCaptures()
        {
            Captures.Clear();

            // Measured off disk rather than summed from the manifests, because the manifests undercount:
            // a file copied out of a folder the game then deleted is still taking up room whether or not
            // its manifest survived, and a single crash on this install left 844 MB behind. Clear Files
            // has an item for these, so the number has to be the one that item will act on.
            captureCandidates = ArtifactCaptureStore.StoredFiles(CaptureRoot);

            var onDisk = ClearedFileStore.DescribeSize(captureCandidates.Sum(f => f.SizeBytes));

            RetentionSummary = ArtifactCaptureRetention.Describe(
                CaptureRoot, retentionSettings.Keep, retentionSettings.KeepDumps);

            var captures = ArtifactCaptureStore.List(CaptureRoot);

            if (captures.Count == 0)
            {
                CapturesSummary = captureCandidates.Count == 0
                    ? Strings.Current["Diagnostics.Captures.NothingYet"]
                    : Strings.Current.Plural(
                        "Diagnostics.Captures.NoIndex", captureCandidates.Count, onDisk, CaptureRoot);

                return;
            }

            var running = captures.Count(c => c.FinishedUtc is null);

            CapturesSummary = Strings.Current.Plural(
                "Diagnostics.Captures.Summary", captures.Count, CaptureRoot, captureCandidates.Count, onDisk)
                + (running > 0
                    ? " " + Strings.Current.Plural("Diagnostics.Captures.StillGoing", running)
                    : string.Empty);

            foreach (var capture in captures)
            {
                var when = capture.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

                var state = capture.FinishedUtc is { } finished
                    ? Strings.Current.Format("Diagnostics.Captures.Row.Finished", $"{finished.ToLocalTime():HH:mm:ss}")
                    : Strings.Current["Diagnostics.Captures.Row.StillCapturing"];

                Captures.Add(new KnownIssueRowViewModel(
                    Strings.Current.Format("Diagnostics.Captures.Row.Heading", capture.Reason, when),
                    capture.Artifacts.Count == 0
                        ? Strings.Current.Format("Diagnostics.Captures.Row.NothingWritten", state)
                        : Strings.Current.Plural(
                              "Diagnostics.Captures.Row.Detail",
                              capture.Artifacts.Count,
                              ClearedFileStore.DescribeSize(capture.TotalBytes),
                              state)
                          + string.Join("; ", capture.Artifacts.Take(8).Select(a => Path.GetFileName(a.OriginalPath)))
                          + (capture.Artifacts.Count > 8
                              ? Strings.Current.Plural("Diagnostics.Captures.Row.AndMore", capture.Artifacts.Count - 8)
                              : string.Empty),
                    capture.Unreadable.Count == 0
                        ? capture.FolderPath
                        : $"{capture.FolderPath}\n{string.Join("\n", capture.Unreadable)}",
                    actions:
                    [
                        new KnownIssueActionViewModel(
                            Strings.Current["Diagnostics.Captures.Row.OpenFolderLabel"],
                            Strings.Current["Diagnostics.Captures.Row.OpenFolderTooltip"],
                            OpenFolderCommand,
                            capture.FolderPath),
                        new KnownIssueActionViewModel(
                            Strings.Current["Diagnostics.Captures.Row.RemoveLabel"],
                            Strings.Current["Diagnostics.Captures.Row.RemoveTooltip"],
                            RemoveCaptureCommand,
                            capture.FolderPath)
                    ]));
            }
        }

        private string Describe(CrashReportSearch search, string install, GameCrashFolderSearch folders)
        {
            var captured = search.Reports.Count(r => r.Source == CrashReportSource.Captured);

            // The list on the left holds managed crash reports, which only exist when a mod's exception
            // reached the top of a thread. A native fault leaves the game's own crash folder instead,
            // and saying "no crash report was found" over one of those is a false statement about the
            // machine, not a cautious one.
            var noManagedReport = folders.Folders.Count > 0
                ? Strings.Current.Plural("Diagnostics.Describe.NoManagedReport.HasFolders", folders.Folders.Count)
                : Strings.Current["Diagnostics.Describe.NoManagedReport.NoFolders"];

            var message = search.Reports.Count == 0
                ? noManagedReport
                : Strings.Current.Plural("Diagnostics.Describe.FoundReports", search.Reports.Count)
                  + (captured > 0
                      ? " " + Strings.Current.Plural("Diagnostics.Describe.CapturedReports", captured)
                      : string.Empty);

            if (string.IsNullOrWhiteSpace(install))
                message += " " + Strings.Current["Diagnostics.Describe.NoInstall"];

            if (search.Reports.Count > 0 && folders.Folders.Count > 0)
                message += " " + Strings.Current.Plural("Diagnostics.Describe.AlsoWroteFolders", folders.Folders.Count);

            if (search.Unreadable.Count > 0)
            {
                message += " " + Strings.Current.Plural(
                    "Diagnostics.Describe.UnreadableFiles", search.Unreadable.Count, string.Join("; ", search.Unreadable));
            }

            if (folders.Unreadable.Count > 0)
                message += $" {string.Join("; ", folders.Unreadable)}";

            return message;
        }

        private static string Describe(LogSearch search)
        {
            var captured = search.Logs.Count(l => l.Source == LogSource.Captured);

            var message = search.Logs.Count == 0
                ? Strings.Current["Diagnostics.Describe.FoundNoLogs"]
                : Strings.Current.Plural("Diagnostics.Describe.FoundLogs", search.Logs.Count)
                  + " " + Strings.Current.Plural(
                      "Diagnostics.Describe.LogsUnknownOwner", search.Logs.Count(l => !l.OwnerIsCertain))
                  + (captured > 0
                      ? " " + Strings.Current.Plural("Diagnostics.Describe.CapturedLogs", captured)
                      : string.Empty);

            if (search.Unreadable.Count == 0)
                return message;

            if (search.Logs.Count == 0)
                message = Strings.Current.Plural("Diagnostics.Describe.NoLogCouldBeListed", search.Unreadable.Count);

            return $"{message} {string.Join("; ", search.Unreadable)}";
        }

        internal static string DescribeSize(long bytes) => bytes switch
        {
            >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.#} MB",
            >= 1024 => $"{bytes / 1024d:0.#} KB",
            _ => Strings.Current.Plural("Diagnostics.Describe.Bytes", bytes)
        };

        private static string ResolveInstallPath()
        {
            var chosen = ShellViewModels.Instance.Environment.GameInstallPath;

            return string.IsNullOrWhiteSpace(chosen) ? GameInstallLocator.Locate() ?? string.Empty : chosen;
        }

        // Which version's user data every read on this page comes out of. The Play page owns the answer
        // because the version dropdown lives there; null means the version being played is the resting
        // one, whose logs, crash folders and load order really are at the canonical machine paths.
        // Without this, every screen here described whichever version happened to be resting.
        private static InstanceDataRoot? ResolveDataRoot() => ShellViewModels.Instance.Environment.ActiveDataRoot;

        partial void OnSelectedReportChanged(CrashReportRowViewModel? value)
        {
            Suspects.Clear();
            NotSuspected.Clear();
            Cleared.Clear();
            VerdictDetail.Clear();
            Unknowns.Clear();
            Candidates.Clear();
            NarrowingSteps.Clear();
            NarrowingRuledOut.Clear();
            NarrowingCaveats.Clear();
            HasNarrowing = false;

            ResetDecompileFor(value);

            if (value is null)
            {
                Headline = string.Empty;
                WhatThrew = string.Empty;
                NextStep = string.Empty;
                SuspectsEmptyText = string.Empty;
                NotSuspectedEmptyText = string.Empty;
                ClearedEmptyText = string.Empty;
                UnknownsEmptyText = string.Empty;
                return;
            }

            var attribution = value.Attribution;

            Headline = attribution.Verdict?.Headline ?? string.Empty;
            WhatThrew = DescribeFault(value);
            NextStep = attribution.Verdict?.NextStep ?? string.Empty;

            foreach (var line in attribution.Verdict?.Detail ?? [])
                VerdictDetail.Add(line);

            var rank = 0;

            foreach (var suspect in attribution.Suspects)
            {
                rank++;

                Suspects.Add(new CrashModuleRowViewModel(
                    $"{rank}. {suspect.ModuleId} - {suspect.Band.ToString().ToUpperInvariant()} (rule {suspect.Rule})",
                    suspect.Evidence,
                    Strings.Current.Format("Diagnostics.Suspects.ToRuleOut", suspect.Disproof)));
            }

            foreach (var module in attribution.NotSuspected)
            {
                NotSuspected.Add(new CrashModuleRowViewModel(
                    Strings.Current.Format("Diagnostics.NotSuspected.Heading", module.ModuleId),
                    module.Evidence,
                    module.Disproof));
            }

            ShowNarrowing(attribution.Narrowing);

            foreach (var cleared in attribution.Exonerations)
                Cleared.Add(cleared);

            foreach (var unknown in attribution.Unknowns)
                Unknowns.Add(unknown);

            SuspectsEmptyText = Suspects.Count > 0
                ? string.Empty
                : Strings.Current["Diagnostics.Suspects.Empty"];

            NotSuspectedEmptyText = NotSuspected.Count > 0
                ? string.Empty
                : Strings.Current["Diagnostics.NotSuspected.Empty"];

            ClearedEmptyText = Cleared.Count > 0
                ? string.Empty
                : Strings.Current["Diagnostics.Cleared.Empty"];

            UnknownsEmptyText = Unknowns.Count > 0 ? string.Empty : Strings.Current["Diagnostics.Unknowns.Empty"];
        }

        // The set, its arithmetic and every caveat that travels with it. Absent rather than empty when
        // there is nothing to narrow: a section that only ever says "nothing here" is a panel taking
        // space on a page the user opened to read an answer.
        private void ShowNarrowing(CandidateNarrowing? narrowing)
        {
            if (narrowing is null)
            {
                NarrowingHeadline = string.Empty;
                NarrowingMechanism = string.Empty;
                NarrowingBoundaryFact = string.Empty;
                NarrowingNecessity = string.Empty;
                NarrowingContainment = string.Empty;
                NarrowingPromotion = string.Empty;
                NarrowingDisproof = string.Empty;
                NarrowingSetIds = string.Empty;

                return;
            }

            NarrowingHeadline = narrowing.Headline;
            NarrowingMechanism = narrowing.Mechanism;
            NarrowingBoundaryFact = narrowing.BoundaryFact;
            NarrowingNecessity = narrowing.Necessity;
            NarrowingContainment = narrowing.Containment;
            NarrowingPromotion = narrowing.Promotion;
            NarrowingDisproof = narrowing.Disproof;
            NarrowingSetIds = narrowing.Set.Ids;

            foreach (var candidate in narrowing.Candidates)
                Candidates.Add(new CandidateRowViewModel(candidate));

            foreach (var step in narrowing.Steps)
                NarrowingSteps.Add(step);

            foreach (var ruled in narrowing.Contradicted)
                NarrowingRuledOut.Add(ruled);

            foreach (var caveat in narrowing.Caveats)
                NarrowingCaveats.Add(caveat);

            HasNarrowing = true;
        }

        [RelayCommand]
        private void CopyCandidateSet() => Copy(NarrowingSetIds, Strings.Current["Diagnostics.Copy.What.CandidateSet"]);

        private static string DescribeFault(CrashReportRowViewModel row)
        {
            var attribution = row.Attribution;

            var lines = new List<string>
            {
                attribution.ThrownTypeFullName.Length == 0
                    ? Strings.Current["Diagnostics.DescribeFault.NoException"]
                    : $"{attribution.ThrownTypeFullName}: {attribution.ThrownMessage}"
            };

            lines.Add(attribution.FaultFrame is null
                ? Strings.Current["Diagnostics.DescribeFault.NoStackTrace"]
                : Strings.Current.Format("Diagnostics.DescribeFault.ThrownIn", attribution.FaultFrame.QualifiedName));

            var outermost = row.Report.Report.Outermost;

            if (outermost is not null
                && !ReferenceEquals(outermost, row.Report.Report.Root)
                && outermost.TypeFullName.Length > 0)
            {
                lines.Add(Strings.Current.Format("Diagnostics.DescribeFault.Outermost", outermost.TypeFullName));
            }

            if (row.Report.Occurrences > 1)
                lines.Add(Strings.Current.Plural("Diagnostics.DescribeFault.RecordedTimes", row.Report.Occurrences));

            lines.Add(Strings.Current.Format("Diagnostics.DescribeFault.ReadFrom", row.Report.Path));

            return string.Join("\n", lines);
        }

        internal static string Describe(CrashReportSource source) => source switch
        {
            CrashReportSource.Game => Strings.Current["Diagnostics.Source.TheGame"],
            CrashReportSource.ButterLib => "ButterLib",
            CrashReportSource.Captured => Strings.Current["Diagnostics.Source.OwnCopy"],
            _ => "CrashDoctor"
        };

        internal static string Describe(LogSource source) => source switch
        {
            LogSource.ButterLib => Strings.Current["Diagnostics.Source.ButterLibFolder"],
            LogSource.Game => Strings.Current["Diagnostics.Source.GameFolder"],
            LogSource.CrashDoctor => Strings.Current["Diagnostics.Source.CrashDoctorFolder"],
            LogSource.Captured => Strings.Current["Diagnostics.Source.OwnCopy"],
            _ => Strings.Current["Diagnostics.Source.ModsOwnFolder"]
        };

        partial void OnSelectedLogChanged(LogRowViewModel? value)
        {
            LogFindings.Clear();

            if (value is null)
            {
                LogFindingsEmptyText = string.Empty;
                return;
            }

            foreach (var finding in value.Scan.Findings)
                LogFindings.Add(new LogFindingRowViewModel(finding));

            LogFindingsEmptyText = value.Scan.Error
                ?? (LogFindings.Count > 0
                    ? string.Empty
                    : Strings.Current["Diagnostics.LogFindings.Empty"]);
        }
    }
}
