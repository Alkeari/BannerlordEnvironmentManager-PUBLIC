using System.Collections.ObjectModel;
using System.Text;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.DryRun;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels
{
    // One row of the timeline. The sentence is the row; everything a person would paste somewhere
    // else is the detail underneath it, and both are selectable.
    public sealed class LaunchTimelineRowViewModel(TimelineEvent record)
    {
        public string At { get; } = record.At;

        public string Headline { get; } = record.Headline;

        public string Detail { get; } = record.Detail;

        public bool IsFailure { get; } = record.Severity == TimelineSeverity.Failure;

        // Resolved from the theme's own resources rather than written as a color here. Amber is the
        // region's single supporting accent and marks the rows that failed; everything else reads as
        // ordinary body text.
        public Microsoft.UI.Xaml.Media.Brush HeadlineBrush { get; } =
            (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                record.Severity == TimelineSeverity.Failure ? "AlkAlertAmberBrush" : "AlkSoftChampagneBrush"];

        public string ModuleId { get; } = record.ModuleId;

        public string Line => At.Length > 0 ? $"{At}  {Headline}" : Headline;
    }

    public sealed class ModulePatchRowViewModel(ModulePatchSummary summary)
    {
        public string ModuleId { get; } = summary.ModuleId.Value;

        public string Line { get; } =
            Strings.Current.Plural("Watch.Timeline.PatchRow.Methods", summary.Methods, summary.ModuleId)
            + " " + Strings.Current.Plural("Watch.Timeline.PatchRow.Patches", summary.Patches);
    }

    public sealed class LaunchRunRowViewModel(LaunchRun run)
    {
        public LaunchRun Run { get; } = run;

        public string Label { get; } =
            $"{run.WhenUtc.ToLocalTime():g}  ({run.RunId})";
    }

    // Starting a watch session and stopping it again. Everything that decides anything lives in
    // Core.DryRun.WatchSession; this reads the environment the user already configured, hands it
    // over, and reports what came back word for word.
    public partial class WatchSessionViewModel : ObservableObject
    {
        private readonly WatchSession session = new(WatchSession.DefaultStateFilePath);

        [ObservableProperty]
        public partial bool IsWatching { get; set; }

        [ObservableProperty]
        public partial string StatusMessage { get; set; } = Strings.Current["Watch.NotWatching"];

        [ObservableProperty]
        public partial InfoBarSeverity Severity { get; set; } = InfoBarSeverity.Informational;

        [ObservableProperty]
        public partial string TracePath { get; set; } = FirstChancePaths.GetDefaultRoot();

        public ObservableCollection<LaunchRunRowViewModel> Runs { get; } = [];

        public ObservableCollection<LaunchTimelineRowViewModel> TimelineRows { get; } = [];

        public ObservableCollection<ModulePatchRowViewModel> PatchesByModule { get; } = [];

        [ObservableProperty]
        public partial int SelectedRunIndex { get; set; } = -1;

        [ObservableProperty]
        public partial bool HasTimeline { get; set; }

        [ObservableProperty]
        public partial string TimelineHeadline { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string TimelineObserved { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string TimelineImplied { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string TimelineCounts { get; set; } = string.Empty;

        // Off by default, because the highlights are the point: a 184-module launch writes 381 rows
        // and the wall of text is the failure mode this feature is most at risk of.
        [ObservableProperty]
        public partial bool ShowEveryEvent { get; set; }

        private LaunchTimeline? timeline;

        private DryRunResult? selectedResult;

        partial void OnSelectedRunIndexChanged(int value) => LoadSelectedRun();

        partial void OnShowEveryEventChanged(bool value) => FillRows();

        // The runs on disk, newest first. One armed watch produces one run per launch rather than one
        // in total, because the companion derives its own run id per process; that is what makes a
        // launch started from Steam an hour later a trail of its own rather than an overwrite.
        public void RefreshTimeline()
        {
            var runs = LaunchRunLocator.List(DryRunPaths.GetDefaultRoot(), FirstChancePaths.GetDefaultRoot());
            var previous = SelectedRunIndex >= 0 && SelectedRunIndex < Runs.Count
                ? Runs[SelectedRunIndex].Run.RunId
                : string.Empty;

            Runs.Clear();

            foreach (var run in runs)
                Runs.Add(new LaunchRunRowViewModel(run));

            var restored = Runs.ToList().FindIndex(r => r.Run.RunId == previous);

            SelectedRunIndex = Runs.Count == 0 ? -1 : restored >= 0 ? restored : 0;

            // Selecting index 0 when it was already 0 raises no change, so the load is asked for
            // directly rather than left to the property.
            LoadSelectedRun();
        }

        private void LoadSelectedRun()
        {
            if (SelectedRunIndex < 0 || SelectedRunIndex >= Runs.Count)
            {
                timeline = null;
                selectedResult = null;
                SaveRegistryCommand.NotifyCanExecuteChanged();
                HasTimeline = false;
                TimelineHeadline = string.Empty;
                TimelineObserved = LaunchTimeline.NotLooked(string.Empty).Observed;
                TimelineImplied = LaunchTimeline.NotLooked(string.Empty).Implied;
                TimelineCounts = string.Empty;
                TimelineRows.Clear();
                PatchesByModule.Clear();
                CopyTimelineCommand.NotifyCanExecuteChanged();

                return;
            }

            timeline = LaunchRunLocator.Read(Runs[SelectedRunIndex].Run);
            selectedResult = DryRunResultFile.Read(Runs[SelectedRunIndex].Run.ResultPath);
            SaveRegistryCommand.NotifyCanExecuteChanged();

            HasTimeline = true;
            TimelineHeadline = timeline.Headline;
            TimelineObserved = timeline.Observed;
            TimelineImplied = timeline.Implied;
            TimelineCounts =
                Strings.Current.Plural("Watch.Timeline.Counts.Events", timeline.Events.Count)
                + " " + Strings.Current.Format("Watch.Timeline.Counts.Highlights", timeline.Highlights.Count)
                + " " + Strings.Current.Plural("Watch.Timeline.Counts.Modules", timeline.PatchesByModule.Count);

            PatchesByModule.Clear();

            foreach (var summary in timeline.PatchesByModule)
                PatchesByModule.Add(new ModulePatchRowViewModel(summary));

            FillRows();
            CopyTimelineCommand.NotifyCanExecuteChanged();
        }

        private void FillRows()
        {
            TimelineRows.Clear();

            if (timeline is null)
                return;

            foreach (var record in ShowEveryEvent ? timeline.Events : timeline.Highlights)
                TimelineRows.Add(new LaunchTimelineRowViewModel(record));
        }

        private bool CanSaveRegistry() => selectedResult?.Patches is { Count: > 0 };

        // The registry answers "what is patching this method" when a crash report is read hours later,
        // and until now the only way to get one was a boot check: a run that exits at the first frame
        // with the module set BEM chose. A watched session captures the same thing from the launch the
        // owner actually plays, so this is the button that makes that one the one on file.
        [RelayCommand(CanExecute = nameof(CanSaveRegistry))]
        private void SaveRegistry()
        {
            var enabled = ShellViewModels.Instance.Environment.BuildEnvironment().Entries
                .Where(entry => entry.IsEnabled && !entry.IsOrphan)
                .Select(entry => entry.Id)
                .ToList();

            var saved = DryRunRegistryCapture.SaveFromResult(
                selectedResult,
                enabled,
                new PatchRegistryStore(
                    PatchRegistryStore.GetDefaultRoot(ShellViewModels.Instance.Environment.ActiveDataRoot)));

            LoggingService.Log($"Watch registry save: {saved.Saved}");

            Severity = saved.Saved ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            StatusMessage = saved.Message;
        }

        private bool CanCopyTimeline() => timeline is not null;

        // Text the user can see is text the user can paste somewhere else, and a timeline exists to
        // be pasted into a bug report.
        [RelayCommand(CanExecute = nameof(CanCopyTimeline))]
        private void CopyTimeline()
        {
            if (timeline is null)
                return;

            var text = new StringBuilder();

            text.AppendLine(Strings.Current.Format("Watch.Timeline.CopyHeader", timeline.RunId));
            text.AppendLine(timeline.Headline);
            text.AppendLine();
            text.AppendLine(Strings.Current.Format("Watch.Timeline.Copy.Observed", timeline.Observed));
            text.AppendLine(Strings.Current.Format("Watch.Timeline.Copy.Implied", timeline.Implied));
            text.AppendLine();

            foreach (var record in timeline.Events)
            {
                text.AppendLine(record.At.Length > 0 ? $"{record.At}  {record.Headline}" : record.Headline);

                if (record.Detail.Length > 0)
                    text.AppendLine("    " + record.Detail.Replace("\n", "\n    "));
            }

            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text.ToString());

            try
            {
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                StatusMessage = Strings.Current["Watch.Timeline.Copy.Success"];
                Severity = InfoBarSeverity.Success;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to copy the launch timeline to the clipboard");
                StatusMessage = Strings.Current["Watch.Timeline.Copy.Failure"];
                Severity = InfoBarSeverity.Error;
            }
        }

        public void Refresh()
        {
            var environment = ShellViewModels.Instance.Environment;
            var state = session.Read();

            IsWatching = session.IsWatching(environment.GameInstallPath);
            StartWatchingCommand.NotifyCanExecuteChanged();
            ArmWatchCommand.NotifyCanExecuteChanged();
            StopWatchingCommand.NotifyCanExecuteChanged();
            LaunchAgainWatchedCommand.NotifyCanExecuteChanged();
            RefreshTimeline();

            // The Play tab's Launch button says whether the next launch is watched, and its
            // banner says whether anything of BEM's own is left in the game or the saved load order.
            // This page is where both change, so it is told rather than left to find out on navigation.
            environment.RefreshCompanionState();

            if (!IsWatching)
            {
                Severity = InfoBarSeverity.Informational;
                StatusMessage = state.IsEmpty
                    ? Strings.Current["Watch.NotWatching"]
                    : Strings.Current["Watch.RecordedButGone"];

                return;
            }

            Severity = InfoBarSeverity.Warning;
            StatusMessage = Strings.Current.Format("Watch.Active", state.RunId);
        }

        // Arming without starting a game, so the launch can be the user's usual one from the
        // Play tab. Same state, same companion, same reversal: this is the Start button with
        // the launch left out.
        [RelayCommand(CanExecute = nameof(CanStartWatching))]
        private void ArmWatch()
        {
            var environment = ShellViewModels.Instance.Environment;
            var payload = CompanionPayload.LocateForInstall(environment.GameInstallPath);

            if (!payload.Found)
            {
                Severity = InfoBarSeverity.Error;
                StatusMessage = Strings.Current.Format("Watch.ArmFailed", payload.Reason);

                return;
            }

            var enabled = environment.BuildEnvironment().Entries
                .Where(entry => entry.IsEnabled && !entry.IsOrphan)
                .ToList();

            var result = session.Arm(
                new WatchRequest(
                    environment.GameInstallPath,
                    enabled,
                    payload.Path!,
                    environment.ExtraArguments,
                    environment.PreferredTarget,
                    environment.CrashHandling));

            LoggingService.Log($"Watch arming: {result.Status}");

            Refresh();

            var armed = result.Status is WatchStartStatus.Armed or WatchStartStatus.AlreadyWatching;

            Severity = armed ? InfoBarSeverity.Warning : InfoBarSeverity.Error;

            StatusMessage = armed && !payload.MatchesInstallPlatform
                ? $"{result.Message} {payload.Reason}"
                : result.Message;
        }

        private bool CanStartWatching() => !IsWatching;

        // The name of the version Play is set to, for the refusal below. The dropdown's label is the
        // version rather than the branch, which is what the user picked and what a message has to
        // name back.
        private static string ActiveVersionName()
        {
            var label = ShellViewModels.Instance.VersionSwitcher.SelectedVersion?.Label;

            return string.IsNullOrWhiteSpace(label) ? Strings.Current["Watch.ActiveVersion.Unset"] : $"'{label}'";
        }

        // Both buttons on this page start a real play session and then let go of it: WatchLaunch
        // returns void, the game is started with UseShellExecute so it outlives BEM, and a launcher
        // target hands off to a game process this code never sees at all. There is therefore no
        // moment BEM can be sure of to take the junctions back down, and junctions left standing
        // after a session is the isolation break the whole feature exists to prevent. Holding the
        // activation scope open for the hours a campaign lasts is not the answer either: the
        // activation lock is exclusive, so it would refuse every dry run, bisection and Play launch
        // for the length of the session, and closing BEM mid-session would still leave the junctions
        // behind. The capability is not lost, only moved: Play holds the scope for its own launch and
        // arms the watch on the way through PlanLaunch.
        private bool RefuseOnAnotherVersion(string what)
        {
            if (ShellViewModels.Instance.Environment.ActiveDataRoot is null)
                return false;

            Severity = InfoBarSeverity.Warning;
            StatusMessage = Strings.Current.Format("Watch.Refuse.Message", ActiveVersionName(), what);

            return true;
        }

        [RelayCommand(CanExecute = nameof(CanStartWatching))]
        private void StartWatching()
        {
            if (RefuseOnAnotherVersion(Strings.Current["Watch.Refuse.Session"]))
                return;

            var environment = ShellViewModels.Instance.Environment;

            if (RunningGame.AnyGameProcessRunning())
            {
                Severity = InfoBarSeverity.Warning;
                StatusMessage = Strings.Current["Watch.AlreadyRunning.Start"];

                return;
            }

            var payload = CompanionPayload.LocateForInstall(environment.GameInstallPath);

            if (!payload.Found)
            {
                Severity = InfoBarSeverity.Error;
                StatusMessage = Strings.Current.Format("Watch.StartFailed", payload.Reason);

                return;
            }

            var enabled = environment.BuildEnvironment().Entries
                .Where(entry => entry.IsEnabled && !entry.IsOrphan)
                .ToList();

            var result = session.Start(
                new WatchRequest(
                    environment.GameInstallPath,
                    enabled,
                    payload.Path!,
                    environment.ExtraArguments,
                    environment.PreferredTarget,
                    environment.CrashHandling),
                WatchProcessLauncher.Create());

            LoggingService.Log($"Watch session start: {result.Status}");

            Refresh();

            Severity = result.Started ? InfoBarSeverity.Warning : InfoBarSeverity.Error;

            // A watch session that records nothing all week because the companion never loaded is the
            // worst version of this: it looks like a quiet game rather than a silent tool.
            StatusMessage = result.Started && !payload.MatchesInstallPlatform
                ? $"{result.Message} {payload.Reason}"
                : result.Message;
        }

        private bool CanRelaunch() => IsWatching;

        [RelayCommand(CanExecute = nameof(CanRelaunch))]
        private void LaunchAgainWatched()
        {
            if (RefuseOnAnotherVersion(Strings.Current["Watch.Refuse.Launch"]))
                return;

            var environment = ShellViewModels.Instance.Environment;

            if (RunningGame.AnyGameProcessRunning())
            {
                Severity = InfoBarSeverity.Warning;
                StatusMessage = Strings.Current["Watch.AlreadyRunning.Relaunch"];

                return;
            }

            var payload = CompanionPayload.LocateForInstall(environment.GameInstallPath);
            var enabled = environment.BuildEnvironment().Entries
                .Where(entry => entry.IsEnabled && !entry.IsOrphan)
                .ToList();

            var result = session.Relaunch(
                new WatchRequest(
                    environment.GameInstallPath,
                    enabled,
                    payload.Found ? payload.Path! : string.Empty,
                    environment.ExtraArguments,
                    environment.PreferredTarget,
                    environment.CrashHandling),
                WatchProcessLauncher.Create());

            LoggingService.Log($"Watched relaunch: {result.Status}");

            Refresh();

            Severity = result.Started ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
            StatusMessage = result.Message;
        }

        private bool CanStopWatching() => IsWatching || !session.Read().IsEmpty;

        [RelayCommand(CanExecute = nameof(CanStopWatching))]
        private void StopWatching()
        {
            var environment = ShellViewModels.Instance.Environment;
            var result = session.Stop(environment.GameInstallPath, environment.SavedLoadOrder);

            LoggingService.Log($"Watch session stop: {result.Status}");

            Refresh();

            Severity = result.Status switch
            {
                WatchStopStatus.Removed => InfoBarSeverity.Success,
                WatchStopStatus.NotWatching => InfoBarSeverity.Informational,
                _ => InfoBarSeverity.Error
            };

            StatusMessage = result.Message;
        }
    }
}
