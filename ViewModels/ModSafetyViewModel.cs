using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Safety;
using BannerlordEnvironmentManager.Services;
using BannerlordEnvironmentManager.Views;
using Microsoft.VisualBasic.FileIO;

namespace BannerlordEnvironmentManager.ViewModels
{
    // The Mod Safety tab. Reads installed modules, Workshop subscriptions and downloaded archives, and
    // reports what matches the trojanized-mod fingerprint.
    //
    // Two triggers feed one picture. The startup scan runs once in the background after the window is
    // up, so the tab already holds a finished result when it is opened. The install check scans an
    // archive before it is extracted and the module folder after, and folds what it learned into the
    // same report rather than keeping a second one.
    //
    // The scan itself is read-only. Every action a finding offers is a separate press, is confirmed,
    // and goes to the Recycle Bin rather than deleting anything.
    public partial class ModSafetyViewModel : BaseViewModel
    {
        private readonly SafetyBlocklistStore blocklistStore = new(SafetyBlocklistStore.DefaultPath());

        // Alarms the user has already read and chosen to stop counting against the Health badge.
        // Suspicious only: a known-bad verdict is a community report by workshop id or file hash, a
        // hard fact rather than a judgment call, and is never offered an accept button at all.
        // Built on each read rather than held, for the same reason Diagnostics does: an alarm accepted
        // on one version says nothing about another, and the version dropdown can change between reads.
        private static AcceptedFindingStore AcceptedFindings => new(
            AcceptedFindingStore.DefaultPath(ShellViewModels.Instance.Environment.ActiveDataRoot));

        private CancellationTokenSource? cancellation;

        private ModSafetyReport? report;

        private SafetyBlocklistState blocklistState = SafetyBlocklistState.Off;

        // Whether everything installed has been looked at, as opposed to only the one archive an
        // install happened to check. Without it a report holding a single clean row would read as a
        // clean bill of health for the whole install.
        private bool scannedInFull;

        public ModSafetyViewModel()
        {
            Title = "Mod Safety";
            BlocklistNote = string.Empty;

            var stored = LoadSettings();

            CheckBlocklist = stored.CheckBlocklist;
            ScanOnStartup = stored.ScanOnStartup;
            CheckArchivesBeforeInstalling = stored.CheckArchivesBeforeInstalling;

            Summary = ScanOnStartup
                ? Strings.Current["Safety.Summary.StartupPending"]
                : Strings.Current["Safety.Summary.AutoScanOff"];
        }

        public ObservableCollection<SafetyRowViewModel> Rows { get; } = [];

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
        public partial bool IsScanning { get; set; }

        [ObservableProperty]
        public partial string Summary { get; set; }

        [ObservableProperty]
        public partial string BlocklistNote { get; set; }

        [ObservableProperty]
        public partial string StatusMessage { get; set; } = string.Empty;

        // What the nav badge outside this tab counts. A finding nobody can see without opening the tab
        // is a finding nobody reaches.
        [ObservableProperty]
        public partial int AlarmCount { get; set; }

        // Never folded into AlarmCount going quiet: an accepted alarm is read, not erased.
        [ObservableProperty]
        public partial int AcceptedAlarmCount { get; set; }

        // On by default: it costs no money, sends no credential, changes nothing outside BEM, and is a
        // single read of one public file. Switching it off leaves every heuristic working.
        [ObservableProperty]
        public partial bool CheckBlocklist { get; set; }

        // On by default: read-only, runs in the background after the window is up, and took 1.9 seconds
        // on a real 203-module install. Off leaves the Scan button doing exactly what it did.
        [ObservableProperty]
        public partial bool ScanOnStartup { get; set; }

        // On by default: an archive is checked before it is extracted, which is the only moment a
        // payload can be stopped before it lands. Nothing is said when nothing is found.
        [ObservableProperty]
        public partial bool CheckArchivesBeforeInstalling { get; set; }

        // On by default. A clean scan of this install listed 228 rows saying nothing was found, which
        // is a check whose expected output on a healthy machine is a wall of text: the one row that
        // mattered would have had 227 identical ones to hide behind. Everything scanned is still one
        // click away, and the count is stated either way.
        [ObservableProperty]
        public partial bool OnlyFindings { get; set; } = true;

        // Empty when there are rows. An empty list with nothing said over it reads as a scan that
        // failed rather than one that found nothing to worry about.
        [ObservableProperty]
        public partial string EmptyListNote { get; set; } = string.Empty;

        // A report describes one version's mods, so a version switch throws it away rather than
        // relabeling it. Nothing is rescanned here: the scan walks every module and every archive, and
        // an empty list saying "press Scan" is honest where a stale list is not. Clearing the report
        // also re-arms the startup scan and the first open of this page.
        public void ClearForVersionChange()
        {
            report = null;
            scannedInFull = false;
            Rows.Clear();
            AlarmCount = 0;
            AcceptedAlarmCount = 0;
            StatusMessage = string.Empty;
            EmptyListNote = string.Empty;
            Summary = Strings.Current["Safety.Summary.VersionChanged"];

            OnPropertyChanged(nameof(HasRows));
            OnPropertyChanged(nameof(HasReport));
        }

        public bool HasRows => Rows.Count > 0;

        public bool HasReport => report is not null;

        // Null when no scan has run, and never an empty list in that case: "found nothing" and "was
        // never asked" are opposite statements to whoever reads a verdict built on this.
        public IReadOnlyList<SafetyScanResult>? LastResults => report?.Results;

        public string AttributionText => Strings.Current["Safety.Attribution"];

        partial void OnCheckBlocklistChanged(bool value) => SaveSettings();

        partial void OnScanOnStartupChanged(bool value) => SaveSettings();

        partial void OnCheckArchivesBeforeInstallingChanged(bool value) => SaveSettings();

        partial void OnOnlyFindingsChanged(bool value)
        {
            _ = value;
            Show();
        }

        private bool CanScan => !IsScanning;

        // Called once from the launch path, after the window is up. Nothing here may be allowed to
        // reach the caller: a scan that fails must cost the tab its result and cost the rest of BEM
        // nothing at all.
        public void StartBackgroundScan()
        {
            try
            {
                if (!ScanOnStartup || IsScanning || report is not null)
                    return;

                _ = ScanAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LoggingService.LogException(ex, "The startup mod safety scan could not be started");
                Summary = Strings.Current.Format("Safety.Summary.StartupScanFailed", ex.Message);
            }
        }

        // What the tab says when it is opened. It never starts a second scan: the startup one has
        // either finished, is still running, or was switched off, and each of those reads differently.
        public void OnOpened()
        {
            if (IsScanning)
                StatusMessage = Strings.Current["Safety.Status.StartupRunning"];
            else if (report is null && ScanOnStartup)
                StartBackgroundScan();
        }

        [RelayCommand(CanExecute = nameof(CanScan))]
        public async Task ScanAsync()
        {
            IsScanning = true;
            StatusMessage = string.Empty;
            Summary = Strings.Current["Safety.Summary.Scanning"];

            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();

            var token = cancellation.Token;

            try
            {
                var (index, state) = await blocklistStore.LoadAsync(
                    CheckBlocklist,
                    CheckBlocklist ? SafetyBlocklistTransport.FetchAsync : null,
                    token);

                blocklistState = state;

                var install = ShellViewModels.Instance.Install;

                // Play's path first: the startup scan runs before any page has navigated, so Install
                // still holds the configured machine install while the alarms it produces are filtered
                // against the selected version's accepted findings. That mismatch is why every alarm
                // accepted on another version came back on a fresh one.
                var gamePath = ShellViewModels.Instance.Environment.GameInstallPath is { Length: > 0 } selected
                    ? selected
                    : install.GameInstallPath;
                var archives = install.ArchivesFolderPath;

                var scanned = await Task.Run(
                    () => ModSafetyScanner.Scan(
                        new ModSafetyRequest(
                            gamePath,
                            archives,
                            index,
                            state,
                            OperatingSystem.IsWindows() ? TaleWorldsSignature.Signer : null,
                            OperatingSystem.IsWindows() ? TaleWorldsSignature.Check : null,
                            InstallViewModel.ExtractCodeFilesForSafetyScan),
                        token),
                    token);

                report = scanned;
                scannedInFull = true;
                BlocklistNote = state.Describe();

                Show();
                Summarize();

                StatusMessage = Strings.Current.Format(
                    "Safety.Status.Scanned",
                    DateTime.Now.ToString("HH:mm:ss"),
                    scanned.Elapsed.TotalSeconds.ToString("F1"));
            }
            catch (OperationCanceledException)
            {
                Summary = Strings.Current["Safety.Summary.Canceled"];
            }
            // Nothing a scan can throw may reach the launch path that starts it, and no failure here
            // is ever reported as a clean result.
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LoggingService.LogException(ex, "The mod safety scan failed");
                Summary = Strings.Current.Format("Safety.Summary.Failed", ex.Message);
            }
            finally
            {
                IsScanning = false;
            }
        }

        [RelayCommand]
        public void CancelScan() => cancellation?.Cancel();

        private bool CanCopyReport => !IsScanning && report is not null;

        [RelayCommand(CanExecute = nameof(CanCopyReport))]
        public void CopyReport() => CopyText(BuildReportText(), Strings.Current["Safety.CopyTarget.Report"]);

        // The blocklist as it was last fetched, read off the cached copy without touching the network.
        // The install path calls this, and a check that has to wait on a web request before it can let
        // an install start is a delay the user would feel every single time.
        internal async Task<SafetyBlocklistIndex> CachedBlocklistAsync(CancellationToken cancellationToken)
        {
            if (!CheckBlocklist)
                return SafetyBlocklistIndex.Empty;

            try
            {
                var (index, _) = await blocklistStore.LoadAsync(false, null, cancellationToken);
                return index;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                LoggingService.LogException(ex, "Failed to read the cached mod safety blocklist");
                return SafetyBlocklistIndex.Empty;
            }
        }

        // What an install check learned, folded into the report the tab shows. Called on the UI thread
        // from the install path so the two triggers converge on one picture rather than two.
        internal void Absorb(IReadOnlyList<SafetyScanResult> results)
        {
            if (results.Count == 0)
                return;

            report = report is null
                ? new ModSafetyReport([.. results], blocklistState, 0, results.Count(r => r.Target.Kind is SafetyTargetKind.Archive))
                : report.With(results);

            Show();
            Summarize();
            OnPropertyChanged(nameof(HasReport));
        }

        private void Summarize()
        {
            if (report is null)
                return;

            Summary = scannedInFull
                ? report.Headline
                : Strings.Current.Format("Safety.Summary.PartialScan", report.Headline);

            OnPropertyChanged(nameof(HasReport));
        }

        private void Show()
        {
            Rows.Clear();

            if (report is null)
                return;

            var acceptedKeys = AcceptedFindings.LoadKeys();

            foreach (var result in report.Results)
            {
                if (OnlyFindings && !result.IsAlarm && !result.NothingCouldBeRead)
                    continue;

                var row = new SafetyRowViewModel(result, this);

                if (row.IsAlarm)
                    row.Accepted = acceptedKeys.Contains(AcceptedFindingStore.KeyFor("ModSafety", row.FindingHeadline));

                Rows.Add(row);
            }

            AlarmCount = Rows.Count(r => r.IsAlarm && !r.Accepted);
            AcceptedAlarmCount = Rows.Count(r => r.IsAlarm && r.Accepted);

            EmptyListNote = Rows.Count > 0
                ? string.Empty
                : report.Results.Count == 0
                    ? Strings.Current["Safety.Empty.NothingScanned"]
                    : Strings.Current.Plural("Safety.Empty.NothingFound", report.Results.Count);

            OnPropertyChanged(nameof(HasRows));
        }

        // The user reading a Suspicious alarm and choosing to stop counting it against the Health
        // badge. Marked on the row immediately rather than waiting for the next scan, same as an
        // install check; the shared Accepted findings list lives on Diagnostics, since it aggregates
        // every family that writes to the same store.
        internal void AcceptFindingRisk(SafetyRowViewModel row)
        {
            if (!row.CanAcceptRisk)
                return;

            var record = AcceptedFindings.Accept([new AcceptedFinding("ModSafety", row.FindingHeadline, DateTimeOffset.UtcNow)]);

            row.Accepted = true;
            AlarmCount = Rows.Count(r => r.IsAlarm && !r.Accepted);
            AcceptedAlarmCount = Rows.Count(r => r.IsAlarm && r.Accepted);
            Report(record.Describe());
            ShellViewModels.Instance.Diagnostics.RefreshAcceptedFindingRows();
        }

        private string BuildReportText()
        {
            if (report is null)
                return string.Empty;

            var text = new StringBuilder();

            text.AppendLine(Strings.Current.Format("Safety.Report.Header", DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
            text.AppendLine(Summary);
            text.AppendLine(BlocklistNote);
            text.AppendLine(
                $"{Strings.Current.Format("Safety.Report.Counts.KnownBad", report.KnownBadCount)}, "
                + $"{Strings.Current.Format("Safety.Report.Counts.Suspicious", report.SuspiciousCount)}, "
                + $"{Strings.Current.Plural("Safety.Report.Counts.CouldNotRead", report.CouldNotLookCount)}, "
                + $"{Strings.Current.Format("Safety.Report.Counts.NothingFound", report.NothingFoundCount)}. "
                + Strings.Current.Plural("Safety.Report.Counts.ModulesSkipped", report.OfficialModulesSkipped));
            text.AppendLine();

            foreach (var result in report.Results)
            {
                if (!result.IsAlarm && !result.NothingCouldBeRead)
                    continue;

                text.AppendLine($"[{result.VerdictText}] {result.Target.Name} ({result.Target.Detail})");
                text.AppendLine($"  {result.Target.Path}");

                foreach (var file in result.Flagged)
                {
                    foreach (var evidence in file.Evidence)
                        text.AppendLine($"  {evidence.Headline}: {evidence.Matched}");

                    text.AppendLine($"    {Strings.Current.Format("Safety.Report.In", file.Location)}");

                    if (file.Sha256 is { } hash)
                        text.AppendLine($"    sha256 {hash}");
                }

                foreach (var unreadable in result.Unreadable)
                    text.AppendLine($"  {Strings.Current.Format("Safety.Report.CouldNotRead", unreadable.Path, unreadable.Reason)}");

                text.AppendLine();
            }

            text.AppendLine(AttributionText);

            return text.ToString();
        }

        internal void Report(string message) => StatusMessage = message;

        // A module or archive just sent to the Recycle Bin is no longer there to alarm about, so the
        // report is corrected immediately rather than left to disagree with reality until the next full
        // scan. Without this, AlarmCount - and with it the shell's Health badge and the Health page's
        // own count - kept naming a finding for something the user had already removed.
        internal void RemoveResult(SafetyScanResult removed)
        {
            if (report is null)
                return;

            report = report.Without(removed);
            Show();
            Summarize();
        }

        internal void CopyText(string text, string what)
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);

            try
            {
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                StatusMessage = Strings.Current.Format("Safety.Copy.Success", what);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to copy a mod safety finding to the clipboard");
                StatusMessage = Strings.Current.Format("Safety.Copy.Failed", what);
            }
        }

        internal void Open(string target)
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });

                StatusMessage = Strings.Current.Format("Safety.Open.Success", target);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                LoggingService.LogException(ex, $"Failed to open '{target}'");
                StatusMessage = Strings.Current.Format("Safety.Open.Failed", target, ex.Message);
            }
        }

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bannerlord Environment Manager",
            "mod-safety-settings.json");

        private sealed record Stored(
            bool CheckBlocklist = true,
            bool ScanOnStartup = true,
            bool CheckArchivesBeforeInstalling = true);

        private static Stored LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath)
                    && JsonSerializer.Deserialize<Stored>(File.ReadAllText(SettingsPath)) is { } stored)
                    return stored;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to read the mod safety settings");
            }

            return new Stored();
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);

                File.WriteAllText(
                    SettingsPath,
                    JsonSerializer.Serialize(new Stored(CheckBlocklist, ScanOnStartup, CheckArchivesBeforeInstalling)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to save the mod safety settings");
            }
        }
    }

    // One scanned module or archive, with the actions that apply to it. Every row has them, not only
    // the ones that were flagged: a mod the scan says nothing about is still a mod the user may want
    // to open, copy or remove.
    public sealed partial class SafetyRowViewModel(SafetyScanResult result, ModSafetyViewModel owner) : ObservableObject
    {
        public SafetyScanResult Result { get; } = result;

        public string Name => Result.Target.Name;

        public string Detail => Result.Target.Detail;

        public string Path => Result.Target.Path;

        public string VerdictText => Result.VerdictText;

        public string VerdictExplanation => Result.VerdictExplanation;

        public string? SignatureNote => Result.SignatureNote;

        public bool HasSignatureNote => Result.SignatureNote is not null;

        public bool IsAlarm => Result.IsAlarm;

        // A community report by workshop id or file hash is a hard fact, not a judgment call, so it is
        // never offered an accept button: muting it would not make the mod less flagged, only stop BEM
        // saying so. Suspicious, the heuristic-only verdict, is the one an owner can read and decide
        // about, same distinction as a preflight Critical versus a Warning.
        public bool CanAcceptRisk => Result.Verdict == SafetyVerdict.Suspicious && !Accepted;

        // Specific to the evidence found rather than to the target alone, so a mod that trades one
        // flagged pattern for a different one later is not silently covered by an acceptance that was
        // only ever about the first. A changed finding is a changed key.
        public string FindingHeadline => Result.Flagged.SelectMany(f => f.Evidence).Any()
            ? string.Join(" | ", Result.Flagged.SelectMany(f => f.Evidence).Select(e => e.Describe()))
            : $"{Name}: {VerdictText}";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanAcceptRisk))]
        [NotifyCanExecuteChangedFor(nameof(AcceptRiskCommand))]
        public partial bool Accepted { get; set; }

        [RelayCommand(CanExecute = nameof(CanAcceptRisk))]
        public void AcceptRisk() => owner.AcceptFindingRisk(this);

        public bool IsArchive => Result.Target.Kind is SafetyTargetKind.Archive;

        public bool CanOpenPage => Result.Target.PageUrl is not null;

        public bool CanDisable => !IsArchive;

        // A report is a public accusation against a named author, so the button is absent unless BEM
        // can name the exact mod. Where it is absent the row says why, because a finding the user
        // cannot pass on is still something they are owed an explanation for.
        public string? WhyNotReportable { get; } = SafetyReport.WhyNotReportable(result);

        public bool CanReport => WhyNotReportable is null;

        public bool HasReportNote => IsAlarm && WhyNotReportable is not null;

        public string ReportNote => WhyNotReportable ?? string.Empty;

        public IReadOnlyList<FlaggedFileViewModel> Flagged { get; } =
            [.. result.Flagged.Select(file => new FlaggedFileViewModel(file, owner))];

        public bool HasFlagged => Result.Flagged.Count > 0;

        public bool HasUnreadable => Result.Unreadable.Count > 0;

        public IReadOnlyList<string> Unreadable { get; } =
            [.. result.Unreadable.Select(file => $"{file.Path}: {file.Reason}")];

        // Same lines as Unreadable, joined into one block: WinUI cannot carry a text selection
        // across separate sibling TextBlocks, so one control per line meant copying the list took
        // one copy per line. One TextBlock over the joined text selects and copies as a whole.
        public string UnreadableText => string.Join("\n", Unreadable);

        public string CapabilityText => Result.Capabilities.Count == 0
            ? Strings.Current["Safety.Row.NoCapabilities"]
            : string.Join(", ", Result.Capabilities.Select(Describe)) + Strings.Current["Safety.Row.CapabilitiesSuffix"];

        public bool HasUnrecognizedUrls => Result.UnrecognizedUrls.Count > 0;

        public string UnrecognizedUrlText => string.Join(", ", Result.UnrecognizedUrls);

        // A list item with no automation name of its own is announced by its ToString, so without this
        // a screen reader reads the type name aloud for every row.
        public override string ToString() => Name;

        // The expander that holds the row is named by its AutomationProperties.Name alone: the text
        // blocks inside its header do not become the name of the toggle button that opens it.
        public string AutomationName => $"{VerdictText}. {Name}. {Detail}";

        private static string Describe(SafetyCapability capability) => capability switch
        {
            SafetyCapability.Network => Strings.Current["Safety.Capability.Network"],
            SafetyCapability.Process => Strings.Current["Safety.Capability.Process"],
            SafetyCapability.Shell => Strings.Current["Safety.Capability.Shell"],
            SafetyCapability.DynamicCode => Strings.Current["Safety.Capability.DynamicCode"],
            _ => Strings.Current["Safety.Capability.Registry"]
        };

        [RelayCommand]
        public void OpenFolder() =>
            owner.Open(IsArchive ? System.IO.Path.GetDirectoryName(Path) ?? Path : Path);

        [RelayCommand(CanExecute = nameof(CanOpenPage))]
        public void OpenPage() => owner.Open(Result.Target.PageUrl!);

        [RelayCommand]
        public void Copy() => owner.CopyText(Describe(), Strings.Current["Safety.CopyTarget.Finding"]);

        // Hands the change to the Play tab rather than writing LauncherData.xml behind its
        // back. It arrives there as a pending edit like any other, so a load order the user has been
        // working on is never written over and never dropped, and Undo and Save both apply to it.
        [RelayCommand(CanExecute = nameof(CanDisable))]
        public async Task DisableAsync()
        {
            if (!await ConfirmAsync(
                    Strings.Current.Format("Safety.DisableConfirm.Title", Name),
                    Strings.Current["Safety.DisableConfirm.Body"],
                    Strings.Current["Safety.DisableConfirm.PrimaryButton"]))
            {
                owner.Report(Strings.Current.Format("Safety.Disable.Kept", Name));
                return;
            }

            var environment = ShellViewModels.Instance.Environment;

            owner.Report(await environment.DisableModuleAsync(Detail, "Mod Safety") switch
            {
                EnvironmentViewModel.ModuleHandoff.Disabled =>
                    Strings.Current.Format("Safety.Disable.Switched", Name),
                EnvironmentViewModel.ModuleHandoff.AlreadyDisabled =>
                    Strings.Current.Format("Safety.Disable.AlreadyOff", Name),
                EnvironmentViewModel.ModuleHandoff.Orphan =>
                    Strings.Current.Format("Safety.Disable.Orphan", Detail),
                EnvironmentViewModel.ModuleHandoff.NotListed =>
                    Strings.Current.Format("Safety.Disable.NotListed", Detail),
                _ => Strings.Current["Safety.Disable.LoadOrderUnreadable"]
            });
        }

        // Builds what BEM would contribute to the community blocklist and hands it to the user. BEM
        // never posts it: the payload is shown in full, and copying it or opening the page is a press.
        //
        // Both halves are on screen and copyable on their own, because the entry and the evidence go to
        // different places in an issue and nobody should have to split a pasted blob by hand.
        [RelayCommand(CanExecute = nameof(CanReport))]
        public async Task ReportUpstreamAsync()
        {
            var draft = SafetyReport.For(Result, DateOnly.FromDateTime(DateTime.Now));

            var body = new StackPanel { Spacing = 10 };

            body.Children.Add(new TextBlock
            {
                Text = Strings.Current.Format("Safety.ReportUpstream.Intro", SafetyReport.WhyNothingIsSent, draft.IdentityText),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });

            body.Children.Add(Draft(Strings.Current["Safety.ReportUpstream.EntryHeader"], draft.Json, "Consolas"));
            body.Children.Add(Draft(Strings.Current["Safety.ReportUpstream.EvidenceHeader"], draft.Evidence, null));

            var copyEntry = new Button { Content = Strings.Current["Safety.CopyEntryButton"] };
            ToolTipService.SetToolTip(copyEntry, Strings.Current["Safety.CopyEntryButton.Tooltip"]);
            copyEntry.Click += (_, _) => owner.CopyText(draft.Json, Strings.Current["Safety.CopyTarget.Entry"]);

            var copyEvidence = new Button { Content = Strings.Current["Safety.CopyEvidenceButton"] };
            ToolTipService.SetToolTip(copyEvidence, Strings.Current["Safety.CopyEvidenceButton.Tooltip"]);
            copyEvidence.Click += (_, _) => owner.CopyText(draft.Evidence, Strings.Current["Safety.CopyTarget.Evidence"]);

            var open = new Button { Content = Strings.Current["Safety.OpenIssuePageButton"] };
            ToolTipService.SetToolTip(open, Strings.Current["Safety.OpenIssuePageButton.Tooltip"]);
            open.Click += (_, _) => owner.Open(SafetyReport.IssuesUrl);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(copyEntry);
            buttons.Children.Add(copyEvidence);
            buttons.Children.Add(open);
            body.Children.Add(buttons);

            var dialog = new ContentDialog
            {
                Title = Strings.Current.Format("Safety.ReportUpstream.DialogTitle", Name),
                Content = new ScrollViewer { MaxHeight = 480, Content = body },
                CloseButtonText = Strings.Current["Safety.ReportUpstream.DoneButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            await DialogText.ShowAsync(dialog);
        }

        // Wraps rather than scrolls sideways, and shows its scroll bar. A read-only TextBox hides both
        // by default, which would leave the end of a 64-character hash off screen on the one surface
        // where every character has to be readable before it is published.
        private static TextBox Draft(string header, string text, string? fontFamily)
        {
            var box = new TextBox
            {
                Header = header,
                Text = text,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 220
            };

            if (fontFamily is not null)
                box.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(fontFamily);

            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            ToolTipService.SetToolTip(box, Strings.Current.Format("Safety.ReportUpstream.DraftTooltip", header));

            return box;
        }

        // The module folder or the archive goes to the Recycle Bin, never to a permanent delete, so a
        // removal the user regrets is undone from Explorer.
        [RelayCommand]
        public async Task RemoveAsync()
        {
            if (IsArchive)
            {
                await RemoveArchiveAsync();
                return;
            }

            // Refused before the confirmation, because deleting the folder cannot remove a subscribed
            // item. This row already carries Open Page, which is where Unsubscribe is.
            if (WorkshopContent.Owns(Path))
            {
                owner.Report(Strings.Current.Format("Core.Modules.Uninstall.Subscribed", Name));
                return;
            }

            var manifestPath = System.IO.Path.Combine(Path, "SubModule.xml");

            if (!SubModuleXmlParser.TryLoad(manifestPath, out var manifest, out var error))
            {
                owner.Report(Strings.Current.Format("Safety.Remove.ManifestUnreadable", manifestPath, error));
                return;
            }

            var plan = ModuleUninstaller.Plan(manifest, []);

            if (!await ConfirmAsync(
                    Strings.Current.Format("Safety.RemoveConfirm.Title", Name),
                    Strings.Current.Plural("Safety.RemoveConfirm.Body", plan.FileCount, ModuleUninstaller.DescribeSize(plan.SizeBytes)),
                    Strings.Current["Safety.RemoveConfirm.PrimaryButton"]))
            {
                owner.Report(Strings.Current.Format("Safety.Remove.Kept", Name));
                return;
            }

            var uninstall = ModuleUninstaller.Uninstall(plan, manifest.Name, RecycleFolder);

            owner.Report(uninstall.Message);

            if (uninstall.Removed)
            {
                await ShellViewModels.Instance.Environment.Refresh();
                owner.RemoveResult(Result);
            }
        }

        private async Task RemoveArchiveAsync()
        {
            if (!await ConfirmAsync(
                    Strings.Current.Format("Safety.RemoveConfirm.Title", Name),
                    Strings.Current["Safety.RemoveArchiveConfirm.Body"],
                    Strings.Current["Safety.RemoveConfirm.PrimaryButton"]))
            {
                owner.Report(Strings.Current.Format("Safety.Remove.Kept", Name));
                return;
            }

            try
            {
                FileSystem.DeleteFile(Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                owner.Report(Strings.Current.Format("Safety.Remove.ArchiveMoved", Name));
                owner.RemoveResult(Result);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                LoggingService.LogException(ex, $"Failed to recycle '{Path}'");
                owner.Report(Strings.Current.Format("Safety.Remove.ArchiveMoveFailed", Name, ex.Message));
            }
        }

        private static void RecycleFolder(string path)
        {
            WorkshopContent.Refuse(path);
            RecycleFolderCore(path);
        }

        private static void RecycleFolderCore(string path) =>
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

        private string Describe()
        {
            var text = new StringBuilder();

            text.AppendLine($"[{VerdictText}] {Name} ({Detail})");
            text.AppendLine(Path);
            text.AppendLine(VerdictExplanation);

            foreach (var file in Result.Flagged)
            {
                foreach (var evidence in file.Evidence)
                    text.AppendLine($"{evidence.Headline}: {evidence.Matched}");

                text.AppendLine($"  {Strings.Current.Format("Safety.Report.In", file.Location)}");

                if (file.Sha256 is { } hash)
                    text.AppendLine($"  sha256 {hash}");
            }

            foreach (var unreadable in Unreadable)
                text.AppendLine(Strings.Current.Format("Safety.Finding.CouldNotRead", unreadable));

            return text.ToString();
        }

        private static async Task<bool> ConfirmAsync(string title, string body, string primary)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = primary,
                CloseButtonText = Strings.Current["Safety.ConfirmDialog.CancelButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
            };

            return await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary;
        }
    }

    public sealed partial class FlaggedFileViewModel(FlaggedFile file, ModSafetyViewModel owner) : ObservableObject
    {
        public string Location => file.Location;

        public string? Sha256 => file.Sha256;

        public bool HasSha256 => file.Sha256 is not null;

        public IReadOnlyList<string> Evidence { get; } = [.. file.Evidence.Select(e => e.Describe())];

        // Same lines as Evidence, joined into one block for the same reason as SafetyRowViewModel's
        // UnreadableText: one TextBlock over the whole thing is selectable end to end.
        public string EvidenceText => string.Join("\n", Evidence);

        public bool CanCheckVirusTotal => file.VirusTotalUrl is not null;

        // Looks the file up by its hash. The file itself never leaves the machine.
        [RelayCommand(CanExecute = nameof(CanCheckVirusTotal))]
        public void CheckVirusTotal() => owner.Open(file.VirusTotalUrl!);

        [RelayCommand]
        public void OpenContainingFolder() =>
            owner.Open(Path.GetDirectoryName(file.FilePath) ?? file.FilePath);

        [RelayCommand(CanExecute = nameof(HasSha256))]
        public void CopyHash() => owner.CopyText(file.Sha256!, Strings.Current["Safety.CopyTarget.Hash"]);
    }
}
