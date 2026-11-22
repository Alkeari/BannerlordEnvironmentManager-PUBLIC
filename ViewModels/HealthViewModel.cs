using System.Collections.ObjectModel;
using System.Collections.Specialized;
using BannerlordEnvironmentManager.Controls;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.DryRun;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Modules.KnownIssues;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace BannerlordEnvironmentManager.ViewModels;

// One button of the batch bar: an action the selected findings share, with how many it applies to.
public sealed class HealthBatchAction(string label, Func<Task> run)
{
    public string Label { get; } = label;

    public IAsyncRelayCommand Command { get; } = new AsyncRelayCommand(run);
}

// The Health destination's answer to "is my setup sound": one count per family of checks, each
// summed from the live collections, plus a one-line summary. These are read-only aggregates, so the
// only thing this view model owns is the moment a change should be re-read to the screen.
public sealed partial class HealthViewModel : ObservableObject
{
    private readonly DiagnosticsViewModel diagnostics;
    private readonly ModSafetyViewModel safety;

    private readonly EnvironmentViewModel? environment;

    public HealthViewModel(DiagnosticsViewModel diagnostics, ModSafetyViewModel safety, EnvironmentViewModel? environment = null)
    {
        this.diagnostics = diagnostics;
        this.safety = safety;
        this.environment = environment;

        diagnostics.PropertyChanged += OnDiagnosticsPropertyChanged;
        safety.PropertyChanged += OnDiagnosticsPropertyChanged;

        diagnostics.XmlOverlapConflicts.CollectionChanged += OnFindingsChanged;
        diagnostics.XmlOverlapOverrides.CollectionChanged += OnFindingsChanged;
        diagnostics.Collisions.CollectionChanged += OnFindingsChanged;
        diagnostics.MergeFaults.CollectionChanged += OnFindingsChanged;
        diagnostics.DryRunFindings.CollectionChanged += OnFindingsChanged;

        // Every list the Health page draws from. A change to any of them redraws the finding list once,
        // on the next dispatcher turn, however many rows the check added in a burst.
        IEnumerable<INotifyCollectionChanged> sources =
        [
            .. InstallSources(),
            diagnostics.XmlOverlapConflicts,
            diagnostics.XmlOverlapOverrides,
            diagnostics.Collisions,
            diagnostics.MergeFaults,
            diagnostics.DryRunFindings,
            diagnostics.PatchOrderIssues,
            diagnostics.RigConflictRows,
            safety.Rows,

            // The launch check's findings belong here too, so Preflight never lists an item that Health does not.
            .. (environment is null ? [] : new INotifyCollectionChanged[] { environment.PreflightFindings }),
        ];

        foreach (var source in sources)
            source.CollectionChanged += OnSourceChanged;

        Families =
        [
            new SegmentedItem("All", string.Empty),
            new SegmentedItem(nameof(HealthFamily.InstallChecks), string.Empty),
            new SegmentedItem(nameof(HealthFamily.Overlaps), string.Empty),
            new SegmentedItem(nameof(HealthFamily.Safety), string.Empty),
            new SegmentedItem(nameof(HealthFamily.Boot), string.Empty),

            // The SubModule Validator lives here as a view rather than a page of its own; it reports no
            // findings into this list, so its chip carries no count.
            new SegmentedItem("Validator", string.Empty),
        ];

        Rebuild();

        // The constructor runs on the UI thread during startup (dump of 2026-11-16 03:26: the crash records
        // were read and parsed here, holding the window for 7 s), so the evidence arrives from a background read.
        ScheduleEvidenceRefresh();
    }

    // The install check families the badge counts, in the order the Install Checks page lists them.
    private IEnumerable<ObservableCollection<KnownIssueRowViewModel>> InstallSources() =>
    [
        diagnostics.DuplicateIds,
        diagnostics.SubModuleClashes,
        diagnostics.HarmonyPatchIssues,
        diagnostics.MissingApiIssues,
        diagnostics.AssemblyIssues,
        diagnostics.ShadowedModules,
        diagnostics.GameAssemblyCopies,
        diagnostics.RigIssues,
    ];

    // Every finding, accepted ones included where they were. Filtered into the two lists below.
    private readonly List<HealthFindingRowViewModel> all = [];

    public ObservableCollection<HealthFindingRowViewModel> ErrorRows { get; } = [];

    public ObservableCollection<HealthFindingRowViewModel> WarningRows { get; } = [];

    // Notes sit in a group of their own, under the warnings, so nothing that is only worth knowing is read
    // as something to worry about.
    public ObservableCollection<HealthFindingRowViewModel> NoteRows { get; } = [];

    // The chip row: All, then one per family, each with its count.
    public ObservableCollection<SegmentedItem> Families { get; }

    [ObservableProperty]
    public partial string SelectedFamily { get; set; } = "All";

    [ObservableProperty]
    public partial bool ShowHidden { get; set; }

    [ObservableProperty]
    public partial int HiddenCount { get; set; }

    [ObservableProperty]
    public partial string ErrorsHeading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WarningsHeading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasErrors { get; set; }

    [ObservableProperty]
    public partial bool HasWarnings { get; set; }

    [ObservableProperty]
    public partial string NotesHeading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasNotes { get; set; }

    [ObservableProperty]
    public partial bool IsClear { get; set; }

    [ObservableProperty]
    public partial string ShowHiddenLabel { get; set; } = string.Empty;

    partial void OnSelectedFamilyChanged(string value)
    {
        _ = value;
        Filter();
    }

    partial void OnShowHiddenChanged(bool value)
    {
        _ = value;
        Filter();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial int SelectedCount { get; set; }

    public bool HasSelection => SelectedCount > 0;

    [ObservableProperty]
    public partial string AcceptSelectedLabel { get; set; } = string.Empty;

    private IEnumerable<HealthFindingRowViewModel> Listed() => ErrorRows.Concat(WarningRows).Concat(NoteRows);

    [ObservableProperty]
    public partial bool HasAcceptable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBatchResult))]
    public partial string BatchResult { get; set; } = string.Empty;

    public bool HasBatchResult => BatchResult.Length > 0;

    // Every action the selected findings share, each once: an outcome possible on several
    // entries is offered, applied to every entry it can happen on and skipped on the rest.
    public ObservableCollection<HealthBatchAction> BatchActions { get; } = [];

    private bool countingPaused;

    private void CountSelected()
    {
        if (countingPaused)
            return;

        var selected = Listed().Where(r => r.IsSelected).ToList();
        var acceptable = HealthGroups.Acceptable(selected, r => r.CanAccept, r => r.IsAccepted).Count;

        SelectedCount = selected.Count;
        HasAcceptable = acceptable > 0;
        AcceptSelectedLabel = Strings.Current.Format("Health.AcceptSelected", acceptable);

        BatchActions.Clear();

        foreach (var group in BatchActionGroups.Group(selected, r => r.Actions, BatchKey))
            BatchActions.Add(ToBatchAction(group));
    }

    // Same key, same effect. Null: no batch form (opening a folder is one window per press).
    private string? BatchKey(KnownIssueActionViewModel action)
    {
        if (ReferenceEquals(action.Command, diagnostics.RemoveShadowedCopiesCommand))
            return "shadowed";

        if (ReferenceEquals(action.Command, diagnostics.ApplyLoadOrderMoveCommand))
            return "move|" + action.Label;

        if (ReferenceEquals(action.Command, diagnostics.DisableRigModuleCommand))
            return "disable|" + action.Parameter;

        if (ReferenceEquals(action.Command, diagnostics.TurnOffRbmPartCommand))
            return "rbm|" + action.Parameter;

        return null;
    }

    private HealthBatchAction ToBatchAction(BatchActionGroup<HealthFindingRowViewModel, KnownIssueActionViewModel> group)
    {
        var skippedRows = SelectedCount - group.Rows.Count;
        var first = group.Actions[0];

        if (group.Key == "shadowed")
        {
            var findings = group.Actions.Select(a => (ShadowedAssembly)a.Parameter!).ToList();

            return new HealthBatchAction(
                Strings.Current.Plural("Diagnostics.Rig.MoveShadowedLabel", findings.Sum(f => f.Removable.Count)),
                async () =>
                {
                    if (await diagnostics.RemoveShadowedCopiesManyAsync(findings) is { } outcome)
                        Finish(outcome, skippedRows);
                });
        }

        var label = group.Rows.Count > 1 ? $"{first.Label} ({group.Rows.Count})" : first.Label;

        if (group.Key.StartsWith("disable|", StringComparison.Ordinal))
        {
            var modules = group.Actions.Select(a => (string)a.Parameter!).ToList();

            return new HealthBatchAction(label, () =>
            {
                Finish(diagnostics.DisableModules(modules), skippedRows);
                return Task.CompletedTask;
            });
        }

        // One setting ends every row of the group, so it is written once whatever the count. Its outcome is
        // the page status the command itself writes.
        if (group.Key.StartsWith("rbm|", StringComparison.Ordinal))
        {
            return new HealthBatchAction(label, () =>
            {
                first.Command.Execute(first.Parameter);
                ClearSelection();
                return Task.CompletedTask;
            });
        }

        // A key added to BatchKey without a branch here once fell through to this cast and threw on every
        // selection (2026-11-11), so only a real move is taken.
        var moves = group.Actions.Select(a => a.Parameter).OfType<LoadOrderMove>().ToList();

        return new HealthBatchAction(label, () =>
        {
            Finish(diagnostics.ApplyLoadOrderMoves(moves), skippedRows);
            return Task.CompletedTask;
        });
    }

    private void Finish((int Done, int Skipped, string Reason) outcome, int rowsWithoutIt)
    {
        var line = Strings.Current.Format("Health.Batch.Result", outcome.Done, outcome.Skipped + rowsWithoutIt);
        BatchResult = outcome.Reason.Length > 0 ? $"{line} {outcome.Reason}" : line;

        // "Done" counts what was changed in the list. While a launcher runs the file waits, and the page must say so.
        if (ShellViewModels.Instance.Environment.HasHeldChanges)
            BatchResult += " " + Strings.Current["Environment.PersistOrder.Held"];
        ClearSelection();
    }

    // Accepts every open finding on the page that shares this one's key.
    private void AcceptAllLike(HealthFindingRowViewModel row) =>
        AcceptMany(HealthGroups.Matching(Listed(), row.GroupKey, r => r.GroupKey));

    // Adds the whole kind to the selection and leaves every other row as it was.
    private void SelectAllLike(HealthFindingRowViewModel row)
    {
        countingPaused = true;

        foreach (var like in HealthGroups.Matching(Listed(), row.GroupKey, r => r.GroupKey, r => r.CanSelect))
            like.IsSelected = true;

        countingPaused = false;
        CountSelected();
    }

    [RelayCommand]
    private void AcceptSelected() => AcceptMany(Listed().Where(r => r.IsSelected));

    [RelayCommand]
    private void SelectAllShown()
    {
        countingPaused = true;

        foreach (var row in Listed().Where(r => r.CanSelect).ToList())
            row.IsSelected = true;

        countingPaused = false;
        CountSelected();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        countingPaused = true;

        foreach (var row in Listed().ToList())
            row.IsSelected = false;

        countingPaused = false;
        CountSelected();
    }

    // Every row handed in is accepted, and says so: accepting a selection accepts every selected item. Each row is first
    // recorded under Health's own key in one write, so a check whose own accept refuses, throws or keys
    // the row differently can no longer leave it behind; the check's own accept still runs so its page
    // agrees. The list is redrawn at once, so the rows leave the moment they are accepted.
    private void AcceptMany(IEnumerable<HealthFindingRowViewModel> rows)
    {
        var chosen = HealthGroups.Acceptable(rows, r => r.CanAccept, r => r.IsAccepted).ToList();

        if (chosen.Count == 0)
            return;

        diagnostics.AcceptHealthFindings([.. chosen.Select(r => (r.SelectionKey, r.Headline))]);

        foreach (var row in chosen)
        {
            try
            {
                row.AcceptAtSource();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                Services.LoggingService.LogException(ex, "A check's own accept failed; Health recorded the acceptance");
            }

            row.IsSelected = false;
            row.IsAccepted = true;
        }

        BatchResult = Strings.Current.Plural("Health.Batch.Accepted", chosen.Count);
        Filter();
        CountSelected();
    }

    private void UndoOne(HealthFindingRowViewModel row)
    {
        diagnostics.ForgetHealthFindings([row.SelectionKey]);

        try
        {
            row.UndoAtSource();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Services.LoggingService.LogException(ex, "A check's own undo failed; Health forgot the acceptance");
        }

        row.IsAccepted = false;
        Filter();
        CountSelected();
    }

    // Health runs by itself: once the load order is read and a few seconds after the last change to it,
    // so a burst of edits runs the checks once; Health runs when BEM starts and when the load order is edited.
    private CancellationTokenSource? scheduled;

    public void ScheduleChecks()
    {
        scheduled?.Cancel();
        var mine = scheduled = new CancellationTokenSource();

        _ = Task.Delay(TimeSpan.FromSeconds(3), mine.Token).ContinueWith(
            _ => App.AppWindow?.DispatcherQueue.TryEnqueue(async () =>
            {
                if (mine.IsCancellationRequested)
                    return;

                // A run already under way covers this change once it finishes; queue one more after it.
                if (IsRunningAllChecks)
                {
                    ScheduleChecks();
                    return;
                }

                try
                {
                    await RunAllChecksAsync();
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    Services.LoggingService.LogException(ex, "Automatic Health checks failed");
                    IsRunningAllChecks = false;
                }
            }),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    // Runs every check the page summarizes: the full install checks and the mod safety scan.
    // Both report through flags that also cover a run already under way when he pressed, so the line
    // beside the button tells the truth from the press until the last check lands, and the command
    // stays disabled for that whole time.
    [RelayCommand]
    private async Task RunAllChecksAsync()
    {
        IsRunningAllChecks = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        if (diagnostics.CheckInstallCommand.CanExecute(null))
            diagnostics.CheckInstallCommand.Execute(null);

        if (safety.ScanCommand.CanExecute(null))
            safety.ScanCommand.Execute(null);

        while (diagnostics.IsCheckingInstall || safety.IsScanning)
        {
            List<string> running = [];

            if (diagnostics.IsCheckingInstall)
                running.Add(Strings.Current.Format("Health.RunAll.Install"));

            if (safety.IsScanning)
                running.Add(Strings.Current.Format("Health.RunAll.Safety"));

            RunAllStatus = Strings.Current.Format("Health.RunAll.Running", string.Join(", ", running), (int)clock.Elapsed.TotalSeconds);
            await Task.Delay(250);
        }

        // The rows are rebuilt at low priority after a check lands; count them once that has happened.
        await Task.Delay(500);

        RunAllStatus = Strings.Current.Format(
            "Health.RunAll.Finished",
            (int)clock.Elapsed.TotalSeconds,
            all.Count(r => !r.IsAccepted && !r.IsHiddenByDefault && r.Severity != FindingSeverity.Note));
        IsRunningAllChecks = false;
    }

    [ObservableProperty]
    public partial bool IsRunningAllChecks { get; set; }

    [ObservableProperty]
    public partial string RunAllStatus { get; set; } = string.Empty;

    private bool rebuildQueued;

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;

        if (rebuildQueued)
            return;

        if (!ScheduleEvidenceRefresh())
            Rebuild();
    }

    // Reading the evidence stamps every flagged module's DLLs and every crash record's modules, and a
    // crash record without stamps costs a signature-checked scan of the whole install. Cold that held
    // the UI thread for 30 s (dump of 2026-11-13 17:32), and warm, one scan per record per rebuild
    // still froze it for 20 s at 18:24. So it is all read here, off the UI thread, and the rebuild
    // only uses the snapshot. False when there is no dispatcher to come back to.
    private bool ScheduleEvidenceRefresh()
    {
        if (rebuildQueued)
            return true;

        if (DispatcherQueue.GetForCurrentThread() is not { } queue)
            return false;

        rebuildQueued = true;

        var install = diagnostics.GameInstallPath;
        var root = environment?.ActiveDataRoot;
        _ = Task.Run(() => ReadEvidenceSnapshot(root, install, stampFromDisk: true)).ContinueWith(task => queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            rebuildQueued = false;

            if (task.IsCompletedSuccessfully)
                ApplyEvidence(task.Result);

            Rebuild();
        }), TaskScheduler.Default);

        return true;
    }

    private sealed record EvidenceSnapshot(IReadOnlyList<LaunchRecord> History, IReadOnlyList<CrashGuardRecord> Crashes, string GameVersion);

    private static EvidenceSnapshot ReadEvidenceSnapshot(BannerlordEnvironmentManager.Core.Instances.InstanceDataRoot? root, string? install, bool stampFromDisk)
    {
        try
        {
            if (root is null)
                return new EvidenceSnapshot([], [], string.Empty);

            var history = new LaunchHistoryStore(LaunchHistoryStore.GetDefaultPath(root)).Read();
            var version = string.IsNullOrEmpty(install) ? default : GameVersionReader.Read(install);
            var crashes = CrashGuardRecord.ReadAll(CrashGuardRecord.DefaultFolder)
                .Where(c => string.Equals(c.GamePath, install, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!string.IsNullOrEmpty(install) && Directory.Exists(Path.Combine(install, "Modules")))
                foreach (var folder in Directory.EnumerateDirectories(Path.Combine(install, "Modules")))
                    LaunchEvidence.StampOf(folder, GameInstallLocator.StandardBinaryFolder);

            return new EvidenceSnapshot(history, stampFromDisk ? CrashGuardRecord.StampAll(crashes) : crashes, version.IsEmpty ? string.Empty : version.ToString());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new EvidenceSnapshot([], [], string.Empty);
        }
    }

    private void ApplyEvidence(EvidenceSnapshot snapshot)
    {
        evidenceHistory = snapshot.History;
        evidenceCrashes = snapshot.Crashes;
        evidenceGameVersion = snapshot.GameVersion;
        evidenceRead = true;
    }

    private void Rebuild()
    {
        // A background check landing mid-selection rebuilds every row, and must not untick what he ticked.
        var ticked = all.Where(r => r.IsSelected).Select(r => r.SelectionKey).ToHashSet(StringComparer.Ordinal);

        all.Clear();
        ReadEvidence();

        foreach (var row in diagnostics.DuplicateIds)
            all.Add(FromIssue(HealthFamily.InstallChecks, HealthFindingKind.DuplicateModuleId, row));

        foreach (var row in diagnostics.SubModuleClashes)
            all.Add(FromIssue(HealthFamily.InstallChecks, HealthFindingKind.SubModuleClassClash, row));

        foreach (var row in diagnostics.HarmonyPatchIssues)
            all.Add(FromIssue(HealthFamily.InstallChecks, HealthFindingKind.HarmonyPatchIssue, row));

        // Behind Show Hidden, as the launch check has it: a reference the metadata cannot resolve is not
        // a call the game makes, and an install plays through dozens of them. The two cases with proof,
        // a call a recent log shows throwing and a build that binds only on another game line, are the
        // launch check's own rows below. The badge does not count these either.
        foreach (var row in diagnostics.MissingApiIssues)
            all.Add(FromIssue(HealthFamily.InstallChecks, HealthFindingKind.MissingApi, row, hidden: true));

        foreach (var row in diagnostics.AssemblyIssues)
            all.Add(FromIssue(HealthFamily.InstallChecks, HealthFindingKind.AssemblyIssue, row));

        foreach (var row in diagnostics.ShadowedModules)
            all.Add(FromIssue(HealthFamily.InstallChecks, HealthFindingKind.ShadowedModule, row));

        foreach (var row in diagnostics.GameAssemblyCopies)
            all.Add(FromIssue(HealthFamily.InstallChecks, HealthFindingKind.GameAssemblyCopy, row));

        foreach (var row in diagnostics.RigIssues)
            all.Add(FromIssue(HealthFamily.InstallChecks, HealthFindingKind.RigIssue, row));

        foreach (var row in diagnostics.RigConflictRows)
            all.Add(FromIssue(HealthFamily.InstallChecks, HealthFindingKind.RigConflict, row));

        foreach (var row in diagnostics.XmlOverlapConflicts)
            all.Add(FromIssue(HealthFamily.Overlaps, HealthFindingKind.XmlOverlapConflict, row, hidden: row.IsExpected));

        // A mod over the base game is how the game is modded: informational, behind Show Hidden.
        foreach (var row in diagnostics.XmlOverlapOverrides)
            all.Add(FromIssue(HealthFamily.Overlaps, HealthFindingKind.XmlOverlapOverride, row, hidden: true));

        // Where the game's XML merge stops: a new campaign is expected to end there, so it is on Health.
        foreach (var row in diagnostics.MergeFaults)
            all.Add(FromIssue(HealthFamily.Overlaps, FindingSeverity.Warning, row));

        foreach (var row in diagnostics.Collisions)
            all.Add(FromIssue(HealthFamily.Overlaps, HealthFindingKind.FileCollision, row, hidden: row.IsExpected));

        // High and Medium change play; Low is several mods on one method with nothing interfering.
        foreach (var row in diagnostics.PatchOrderIssues)
            all.Add(FromIssue(HealthFamily.Overlaps, HealthFindingKind.PatchOrder, row));

        foreach (var row in safety.Rows.Where(r => r.IsAlarm))
        {
            all.Add(new HealthFindingRowViewModel(
                HealthFamily.Safety,
                // One piece of text in a mod is a Note; what the scan was written to find stays a Warning.
                HealthJudgement.Of(row.Result) == HealthVerdict.Note ? FindingSeverity.Note : Severity(HealthFindingKind.SafetyAlarm),
                row.FindingHeadline,
                row.VerdictExplanation,
                row.Path,
                row.Accepted,
                canAccept: true,
                [],
                row,
                r => ((SafetyRowViewModel)r.Source).AcceptRisk(),
                r => safety.UndoAcceptFindingRisk((SafetyRowViewModel)r.Source)));
        }

        foreach (var row in diagnostics.DryRunFindings)
            all.Add(FromIssue(HealthFamily.Boot, HealthFindingKind.DryRunFinding, row));

        // What the launch check on Play found, with its own fix and Accept the Risk, so nothing it warns
        // about is seen only there. Accepting here accepts it on Play as well.
        foreach (var row in environment?.PreflightFindings.Where(r => r.Finding is not null && r.CanAccept) ?? [])
        {
            IReadOnlyList<KnownIssueActionViewModel> actions = row.IsFixable
                ? [new KnownIssueActionViewModel(row.FixLabel, row.FixLabel, new RelayCommand(row.Fix))]
                : [];

            all.Add(new HealthFindingRowViewModel(
                HealthFamily.Boot,
                row.Severity,
                row.Headline,
                row.Detail,
                string.Empty,
                row.IsAccepted,
                canAccept: true,
                actions,
                row,
                r => ((PreflightRowViewModel)r.Source).AcceptRisk(),
                r => ((PreflightRowViewModel)r.Source).Undo(),
                row.IsHiddenByDefault));
        }

        // Health's own record wins: a row accepted here stays accepted on every rerun, whatever key its
        // check would derive for it.
        var acceptedHere = diagnostics.LoadAcceptedKeys();

        foreach (var row in all)
        {
            if (row.CanAccept && acceptedHere.Contains(DiagnosticsViewModel.HealthKey(row.SelectionKey)))
                row.IsAccepted = true;

            row.AcceptHandler = r => AcceptMany([r]);
            row.UndoHandler = UndoOne;
            row.CanSelect = row.CanAccept || row.Actions.Any(a => BatchKey(a) is not null);
            row.IsSelected = row.CanSelect && ticked.Contains(row.SelectionKey);
        }

        foreach (var family in Families)
        {
            var count = family.Key == "All"
                ? all.Count
                : all.Count(r => r.Family.ToString() == family.Key);

            family.Label = Strings.Current.Format(LabelKeyFor(family.Key), count);
        }

        Filter();
    }

    private static string LabelKeyFor(string family) => family switch
    {
        nameof(HealthFamily.InstallChecks) => "Health.Chip.InstallChecks",
        nameof(HealthFamily.Overlaps) => "Health.Chip.Overlaps",
        nameof(HealthFamily.Safety) => "Health.Chip.Safety",
        nameof(HealthFamily.Boot) => "Health.Chip.Boot",
        "Validator" => "Health.Chip.Validator",
        "CrashReports" => "Health.Chip.CrashReports",
        _ => "Health.Chip.All",
    };

    // The Health page's own checks take their severity from the one table in Core, never from a literal here.
    private static FindingSeverity Severity(HealthFindingKind kind) => HealthSeverities.For(kind) switch
    {
        IssueSeverity.Error => FindingSeverity.Error,
        IssueSeverity.Warning => FindingSeverity.Warning,
        _ => FindingSeverity.Note,
    };

    // The launches and crashes on record, read once per rebuild, so any row can be raised to an Error by a
    // crash of the same mod file on the same game, or kept from one by a run that played fine with it.
    private IReadOnlyList<LaunchRecord> evidenceHistory = [];
    private IReadOnlyList<CrashGuardRecord> evidenceCrashes = [];
    private string evidenceGameVersion = string.Empty;

    private bool evidenceRead;

    // Runs inside every rebuild, on the UI thread, so it only reads the snapshot the background step made.
    // The first rebuild, in the constructor, has none yet and starts empty.
    private void ReadEvidence()
    {
        if (evidenceRead)
            return;

        // No disk read on the UI thread: until the background snapshot lands the evidence is empty. With no
        // dispatcher there is no UI thread to protect and no later refresh, so it is read here.
        if (DispatcherQueue.GetForCurrentThread() is null)
            ApplyEvidence(ReadEvidenceSnapshot(environment?.ActiveDataRoot, diagnostics.GameInstallPath, stampFromDisk: true));
        else
            ApplyEvidence(new EvidenceSnapshot([], [], string.Empty));
    }

    private HealthFindingRowViewModel FromIssue(HealthFamily family, HealthFindingKind kind, KnownIssueRowViewModel row, bool hidden = false)
    {
        var severity = Severity(kind);

        if (row.ModuleId.Length > 0 && evidenceGameVersion.Length > 0)
        {
            var stamp = LaunchEvidence.StampOf(row.ModuleFolderPath, GameInstallLocator.StandardBinaryFolder);
            var verdict = FindingEvidence.Judge(kind, row.ModuleId, row.RawHeading, stamp, evidenceGameVersion, evidenceHistory, evidenceCrashes, row is PatchOrderRowViewModel order ? order.Target : null);

            if (verdict.Adjustment != EvidenceAdjustment.None)
            {
                row.Judged = verdict.Apply(row.Judged ?? HealthSeverities.For(kind));
                return FromIssue(family, severity, row, hidden, verdict.Reason);
            }
        }

        return FromIssue(family, severity, row, hidden);
    }

    private HealthFindingRowViewModel FromIssue(HealthFamily family, FindingSeverity severity, KnownIssueRowViewModel row, bool hidden = false, string proof = "")
    {
        // A row judged on what this install proves carries its own severity; the table is the default.
        severity = row.Judged switch
        {
            IssueSeverity.Error => FindingSeverity.Error,
            IssueSeverity.Warning => FindingSeverity.Warning,
            IssueSeverity.Information => FindingSeverity.Note,
            _ => severity,
        };

        // The player can accept the risk on every finding, a Note included.
        const bool canAccept = true;

        diagnostics.EnsureAcceptable(row);

        return new(
            family,
            severity,
            row.RawHeading,
            // An Error or a refusal of one says which proof made it so, ahead of everything else.
            proof.Length > 0 ? proof : row.Note.Length > 0 ? row.Note : row.Detail,
            proof.Length > 0 ? (row.Note.Length > 0 ? row.Note + "\n" + row.Detail : row.Detail) : row.Note.Length > 0 ? row.Detail : string.Empty,
            row.Accepted,
            canAccept,
            row.Actions,
            row,
            r => ((KnownIssueRowViewModel)r.Source).AcceptRisk(),
            r => diagnostics.UndoAcceptFinding((KnownIssueRowViewModel)r.Source),
            hidden);
    }

    // Raised each time the open lists are refilled, so Play can show a module's own findings on its row.
    public event EventHandler? ModuleFindingsChanged;

    // The open findings whose check knows which one module they are about: Core's ModuleProblems turns
    // them into row reasons, and the first action that changes something is the button beside it.
    public IReadOnlyList<EnvironmentViewModel.ModuleFindingSource> ModuleFindings() =>
    [
        .. all
            .Where(r => !r.IsAccepted && !r.IsHiddenByDefault && r.Severity != FindingSeverity.Note
                && r.Source is KnownIssueRowViewModel { ModuleId.Length: > 0 })
            .Select(r => new EnvironmentViewModel.ModuleFindingSource(
                new ModuleFinding(
                    new ModuleId(((KnownIssueRowViewModel)r.Source).ModuleId),
                    r.IsError ? IssueSeverity.Error : IssueSeverity.Warning,
                    r.Headline,
                    r.SelectionKey),
                r.PrimaryActions.FirstOrDefault())),
    ];

    private int filterRetries;

    private void Filter()
    {
        var shown = all.Where(r => SelectedFamily == "All" || r.Family.ToString() == SelectedFamily).ToList();

        HiddenCount = shown.Count(r => r.IsHiddenByDefault);
        ShowHiddenLabel = Strings.Current.Plural("Health.ShowHidden", HiddenCount);

        // An accepted finding leaves the open lists for the Accepted Findings section beside the title,
        // where Undo brings it back: accepted risks leave the main screen and sit in a small nested section.
        var visible = shown.Where(r => !r.IsAccepted && (ShowHidden || !r.IsHiddenByDefault)).ToList();

        // Findings that say the same thing about the same mods are counted together, so each carries
        // one Accept the Risk for All Like This button instead of 144 separate presses.
        var groups = visible.Where(r => r.CanAccept).GroupBy(r => r.GroupKey).ToDictionary(g => g.Key, g => g.Count());

        foreach (var row in visible)
        {
            row.SimilarCount = row.CanAccept && groups.TryGetValue(row.GroupKey, out var n) ? n : 1;
            row.AcceptSimilarLabel = Strings.Current.Format("Health.AcceptSimilar", row.SimilarCount);
            row.AcceptSimilarHandler = AcceptAllLike;
            row.SelectSimilarLabel = Strings.Current.Format("Health.SelectSimilar", row.SimilarCount);
            row.SelectSimilarHandler = SelectAllLike;
            row.SelectionChanged = CountSelected;
        }

        // ItemsRepeater refuses a source change while it is laying out (E_FAIL, "Unspecified error"). The
        // press that got here has already been taken, so the refill waits for the layout to finish
        // rather than reaching him as an application error.
        try
        {
            ErrorRows.Clear();
            WarningRows.Clear();
            NoteRows.Clear();

            foreach (var row in visible)
                (row.IsError ? ErrorRows : row.Severity == FindingSeverity.Note ? NoteRows : WarningRows).Add(row);

            filterRetries = 0;
        }
        catch (System.Runtime.InteropServices.COMException ex) when (filterRetries < 5
            && DispatcherQueue.GetForCurrentThread() is not null)
        {
            filterRetries++;
            Services.LoggingService.Log($"Health list refill deferred ({filterRetries}): {ex.Message}", Services.LogLevel.Warn);
            DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, Filter);
            return;
        }

        ModuleFindingsChanged?.Invoke(this, EventArgs.Empty);

        HasErrors = ErrorRows.Count > 0;
        HasWarnings = WarningRows.Count > 0;
        HasNotes = NoteRows.Count > 0;

        // A note asks nothing of him, so an install with only notes is clear.
        IsClear = ErrorRows.Count == 0 && WarningRows.Count == 0;
        ErrorsHeading = Heading("Health.Group.Errors", shown.Where(r => r.IsError && (ShowHidden || !r.IsHiddenByDefault)).ToList());
        WarningsHeading = Heading(
            "Health.Group.Warnings",
            shown.Where(r => !r.IsError && r.Severity != FindingSeverity.Note && (ShowHidden || !r.IsHiddenByDefault)).ToList());
        NotesHeading = Strings.Current.Format("Health.Group.Notes", NoteRows.Count);
        CountSelected();
    }

    // An accepted finding still counts in its group, and the heading says how many of them are accepted,
    // so the number is honest and the reader can see why a group with findings asks nothing of them.
    private static string Heading(string key, IReadOnlyList<HealthFindingRowViewModel> rows)
    {
        var head = Strings.Current.Format(key, rows.Count);
        var accepted = rows.Count(r => r.IsAccepted);

        return accepted == 0 ? head : $"{head} {Strings.Current.Format("Health.Group.AcceptedShare", accepted)}";
    }

    public int InstallChecksCount => diagnostics.InstallCheckFindingCount + diagnostics.AcceptedInstallCheckCount;

    public int OverlapsCount =>
        diagnostics.XmlOverlapConflicts.Count + diagnostics.XmlOverlapOverrides.Count + diagnostics.Collisions.Count
        + diagnostics.PatchOrderIssues.Count + diagnostics.MergeFaults.Count;

    public int SafetyCount => safety.AlarmCount + safety.AcceptedAlarmCount;

    public int BootCount => diagnostics.DryRunFindings.Count;

    // Never folded into the counts above going quiet: an accepted finding is read, not erased, and a
    // badge that only ever shrank when something was accepted would read as a clean install that never
    // had anything to accept.
    public int AcceptedCount =>
        diagnostics.AcceptedInstallCheckCount + diagnostics.AcceptedBootCheckCount
        + diagnostics.AcceptedXmlOverlapCount + safety.AcceptedAlarmCount;

    public string HealthSummary
    {
        get
        {
            var total = InstallChecksCount + OverlapsCount + SafetyCount + BootCount;
            var head = total == 0
                ? Strings.Current["Health.Summary.NoFindings"]
                : Strings.Current.Plural("Health.Summary.FindingCount", total);

            return AcceptedCount == 0
                ? head
                : $"{head} {Strings.Current.Plural("Health.Summary.AcceptedSuffix", AcceptedCount)}";
        }
    }

    public void Refresh() => _ = diagnostics.CheckInstallQuicklyAsync();

    private void OnDiagnosticsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiagnosticsViewModel.AcceptedXmlOverlapCount)
            or nameof(DiagnosticsViewModel.AcceptedInstallCheckCount)
            or nameof(DiagnosticsViewModel.AcceptedBootCheckCount)
            or nameof(ModSafetyViewModel.AcceptedAlarmCount))
        {
            Rebuild();
            RaiseAll();
            return;
        }

        if (e.PropertyName is nameof(DiagnosticsViewModel.InstallCheckFindingCount)
            or nameof(ModSafetyViewModel.AlarmCount))
        {
            RaiseAll();
        }
    }

    private void OnFindingsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RaiseAll();

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(InstallChecksCount));
        OnPropertyChanged(nameof(OverlapsCount));
        OnPropertyChanged(nameof(SafetyCount));
        OnPropertyChanged(nameof(BootCount));
        OnPropertyChanged(nameof(AcceptedCount));
        OnPropertyChanged(nameof(HealthSummary));
    }
}
