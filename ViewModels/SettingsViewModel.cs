using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using BannerlordEnvironmentManager.Core.Butr;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.GameSettings;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Nexus;
using BannerlordEnvironmentManager.Core.Toolkit;
using BannerlordEnvironmentManager.Services;

using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.ViewModels
{
    public sealed partial class ButrScoreRow(ButrModuleScore score)
    {
        public string ModuleName { get; } = score.DisplayName;

        public string Detail { get; } = score.RecommendedVersion is { } recommended
            ? Strings.Current.Format(
                "Settings.ButrScore.DetailWithRecommendation",
                score.Compatibility.ToString("0.#", Strings.Current.Culture),
                recommended,
                (score.RecommendedCompatibility ?? 0).ToString("0.#", Strings.Current.Culture))
            : Strings.Current.Format(
                "Settings.ButrScore.DetailNoRecommendation",
                score.Compatibility.ToString("0.#", Strings.Current.Culture));
    }

    public sealed partial class NexusUpdateRow(NexusModuleUpdate update)
    {
        public string ModuleName { get; } = update.ModuleName;

        public string ModuleId { get; } = update.ModuleId;

        public string ModPageUrl { get; } = update.ModPageUrl;

        public string Detail { get; } = Strings.Current.Format(
            "Settings.NexusUpdateRow.Detail",
            update.NexusModId,
            update.LatestFileUpdate.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
    }

    // The network-facing settings, all of them behind one disclosure. Nothing in this class runs
    // until the Settings page or the Crash Reports page is opened, so a session that opens neither
    // pays nothing. Crash Reports is the second one because the crash contribution below is shown
    // there, with the crashes it is built from, rather than here.
    public sealed partial class SettingsViewModel : BaseViewModel
    {
        private static readonly Lazy<SettingsViewModel> Singleton = new(() => new SettingsViewModel());

        private bool revertingShareGameSettings;

        private readonly NexusOptionsStore nexusOptionsStore;

        // Named off the machine root rather than off a store that is now per version: whether dividers
        // are shown is a display preference and stays machine-wide, and deriving its folder from the
        // parent of a per-version path would move the file the moment a managed version was selected.
        private readonly LoadOrderDividerOptionsStore dividerOptionsStore =
            new(Path.Combine(InstanceStateFolder.MachineRoot(), "load-order-settings.json"));

        private readonly ToolkitOptionsStore toolkitOptionsStore = new(ToolkitOptionsStore.DefaultPath);

        // Sharing is one switch over every instance, so it lives with the instance system's own
        // settings rather than in a file of its own, and the shared values sit beside them where no
        // instance owns them and removing an instance cannot take them away.
        private readonly InstanceSettingsStore instanceSettingsStore = new();

        private readonly GameSettingsSync gameSettingsSync =
            GameSettingsSync.Beside(InstanceSettingsStore.DefaultPath);

        private readonly NexusApiKeyStore keyStore;

        private readonly NexusUpdateCheck updateCheck;

        private readonly NexusSsoSession ssoSession;

        private readonly ButrOptionsStore butrOptionsStore;

        private readonly ButrCompatibility butrCompatibility;

        private readonly ButrContributionOptionsStore butrContributionStore;

        private readonly ButrContribution butrContribution;

        private CancellationTokenSource? nexusCancellation;

        private CancellationTokenSource? ssoCancellation;

        private CancellationTokenSource? butrCancellation;

        private CancellationTokenSource? contributionCancellation;

        private ButrPayload? preparedContribution;

        private bool loading;

        public SettingsViewModel()
        {
            var directory = NexusApiKeyStore.DefaultDirectory;

            var protector = new WindowsSecretProtector();

            nexusOptionsStore = new NexusOptionsStore(Path.Combine(directory, NexusOptionsStore.FileName));
            keyStore = new NexusApiKeyStore(directory, protector);
            updateCheck = new NexusUpdateCheck(
                new NexusClient(new NexusHttpTransport()),
                new NexusUpdateCache(Path.Combine(directory, "nexus-update-cache.json")));

            // No socket is opened until the user presses sign in, so this costs nothing to hold.
            ssoSession = new NexusSsoSession(
                new NexusSsoWebSocketFactory(),
                new NexusSsoBrowser(),
                new NexusSsoTokenStore(directory, protector),
                keyStore);

            butrOptionsStore = new ButrOptionsStore(Path.Combine(directory, ButrOptionsStore.FileName));
            butrCompatibility = new ButrCompatibility(
                new ButrClient(new ButrHttpTransport()),
                new ButrScoreCache(Path.Combine(directory, "butr-score-cache.json")));

            butrContributionStore = new ButrContributionOptionsStore(
                Path.Combine(directory, ButrContributionOptionsStore.FileName));
            butrContribution = new ButrContribution(
                new ButrCrashHttpTransport(),
                new ButrSubmissionLedger(Path.Combine(directory, ButrSubmissionLedger.FileName)));

            loading = true;

            var options = nexusOptionsStore.Load();
            NexusUpdateCheckEnabled = options.UpdateCheckEnabled;
            BemUpdateCheckAtStartup = options.BemUpdateCheckAtStartup;
            NexusPeriodIndex = (int)options.Period;
            ButrScoresEnabled = butrOptionsStore.Load().Enabled;
            ButrContributionEnabled = butrContributionStore.Load().Enabled;
            KeepDividersAnchoredOnAutoSort = dividerOptionsStore.Load().KeepAnchoredOnAutoSort;
            InstallSevenZipOnDemand = toolkitOptionsStore.Load().InstallSevenZipOnDemand;
            ShareGameSettings = instanceSettingsStore.Read().ShareGameSettings;

            loading = false;

            RefreshKeyStatus();
            RefreshNxmStatus();
        }

        public static SettingsViewModel Instance => Singleton.Value;

        public ObservableCollection<NexusUpdateRow> NexusUpdates { get; } = [];

        // Signing in needs an application identifier Nexus issues only after approving the
        // application, and BEM has not been issued one. An unapproved identifier is refused by the
        // service, so the button stays visible but uninteractable until Nexus issues one, and its
        // tooltip and the note below both say why.
        public bool NexusSsoAvailable => NexusSso.IsEnabled;

        public bool NexusSsoUnavailable => !NexusSso.IsEnabled;

        public string NexusSsoUnavailableNote => Strings.Current["Settings.NexusSso.UnavailableNote"];

        public string NexusSsoButtonTooltip => NexusSso.IsEnabled
            ? Strings.Current["Settings.NexusSsoButton.Tooltip"]
            : Strings.Current["Settings.NexusSsoButton.Tooltip.Unavailable"];

        [ObservableProperty]
        public partial bool NexusUpdateCheckEnabled { get; set; }

        [ObservableProperty]
        public partial bool BemUpdateCheckAtStartup { get; set; }

        partial void OnBemUpdateCheckAtStartupChanged(bool value)
        {
            _ = value;
            SaveNexusOptions();
        }

        [ObservableProperty]
        public partial bool KeepDividersAnchoredOnAutoSort { get; set; }

        [ObservableProperty]
        public partial bool InstallSevenZipOnDemand { get; set; }

        [ObservableProperty]
        public partial bool ShareGameSettings { get; set; }

        [ObservableProperty]
        public partial string SharedGameSettingsStatusMessage { get; set; } = string.Empty;

        // Printed beside the checkbox before the box is ever ticked, so the command is read and refused
        // rather than trusted. It is the same command the Toolkit Install button runs.
        public string SevenZipInstallCommandText
        {
            get
            {
                if (ToolkitCatalog.ById(ToolkitCatalog.SevenZipId) is not { } sevenZip)
                    return ToolkitInstaller.NoWinGet;

                return ToolkitCommands.Install(sevenZip, WinGetLocator.Locate()) is { } command
                    ? Strings.Current.Format("Settings.SevenZip.CommandText", command.DisplayText)
                    : ToolkitInstaller.NoWinGet;
            }
        }

        [ObservableProperty]
        public partial int NexusPeriodIndex { get; set; }

        [ObservableProperty]
        public partial string NexusKeyStatus { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool HasNexusKey { get; set; }

        [ObservableProperty]
        public partial string NexusStatusMessage { get; set; } = string.Empty;

        // The check used to report itself as one paragraph, which buried the tally and the quota
        // figures in each other and made the numbers impossible to pick out or copy on their own.
        // The same words are now carried a fact at a time so each one is findable and selectable.
        public ObservableCollection<string> NexusResultLines { get; } = [];

        [ObservableProperty]
        public partial string NexusDataFetched { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NexusQuotaHourly { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NexusQuotaDaily { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NexusQuotaNote { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool NexusBusy { get; set; }

        [ObservableProperty]
        public partial string NexusSsoMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool NexusSsoBusy { get; set; }

        [ObservableProperty]
        public partial bool NxmHandlerEnabled { get; set; }

        [ObservableProperty]
        public partial string NxmStatus { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NxmPreviousHandler { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NxmArchivesFolder { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool ButrScoresEnabled { get; set; }

        [ObservableProperty]
        public partial string ButrStatusMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ButrModulesWithoutData { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool ButrBusy { get; set; }

        public ObservableCollection<ButrScoreRow> ButrScores { get; } = [];

        partial void OnButrScoresEnabledChanged(bool value)
        {
            if (loading)
                return;

            butrOptionsStore.Save(butrOptionsStore.Load() with { Enabled = value });
        }

        [RelayCommand]
        private void OpenButrProject() => OpenUrl(ButrEndpoints.ProjectUrl, Strings.Current["Settings.Butr.ProjectLinkLabel"]);

        [RelayCommand]
        private async Task FetchButrScoresAsync()
        {
            butrCancellation?.Cancel();
            butrCancellation?.Dispose();
            butrCancellation = new CancellationTokenSource();

            ButrBusy = true;

            try
            {
                var options = butrOptionsStore.Load();
                var modules = InstalledModules();

                var report = await butrCompatibility.RunAsync(
                    new ButrRequest(
                        [.. modules.Where(module => !module.IsOfficial)
                            .Select(module => new ButrModuleRequest(module.Id.Value, module.VersionText ?? module.Version.ToString(), module.Name))],
                        GameVersionText(modules),
                        options.Enabled,
                        options.CacheLifetime,
                        DateTimeOffset.UtcNow,
                        ForceRefresh: true),
                    butrCancellation.Token);

                ButrScores.Clear();

                foreach (var score in report.Scores.OrderBy(score => score.Compatibility))
                    ButrScores.Add(new ButrScoreRow(score));

                ButrStatusMessage = report.FetchedUtc is { } fetched
                    ? Strings.Current.Format(
                        "Settings.Butr.DataFetched", report.Message, fetched.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                    : report.Message;

                ButrModulesWithoutData = report.ModulesWithoutData.Count == 0
                    ? string.Empty
                    : Strings.Current.Format("Settings.Butr.NoDataFor", string.Join(", ", report.ModulesWithoutData));
            }
            catch (OperationCanceledException)
            {
                ButrStatusMessage = Strings.Current["Settings.Butr.Canceled"];
            }
            finally
            {
                ButrBusy = false;
            }
        }

        [RelayCommand]
        private void CancelButrFetch() => butrCancellation?.Cancel();

        [ObservableProperty]
        public partial bool ButrContributionEnabled { get; set; }

        [ObservableProperty]
        public partial string ButrContributionStatusMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ButrContributionPayload { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SendButrContributionCommand))]
        [NotifyCanExecuteChangedFor(nameof(DiscardButrContributionCommand))]
        public partial bool ButrContributionReady { get; set; }

        [ObservableProperty]
        public partial bool ButrContributionBusy { get; set; }

        partial void OnButrContributionEnabledChanged(bool value)
        {
            if (loading)
                return;

            butrContributionStore.Save(butrContributionStore.Load() with { Enabled = value });

            ClearPreparedContribution();

            ButrContributionStatusMessage = value
                ? Strings.Current["Settings.ButrContribution.OnNote"]
                : string.Empty;
        }

        [RelayCommand]
        private void OpenButrCrashProject() => OpenUrl(ButrEndpoints.CrashProjectUrl, Strings.Current["Settings.Butr.CrashProjectLinkLabel"]);

        // Builds the report and shows it. Nothing leaves the machine here: the user reads the exact
        // bytes first, every time, and pressing send posts that same text and nothing else.
        [RelayCommand]
        private async Task PrepareButrContributionAsync()
        {
            ButrContributionBusy = true;
            ClearPreparedContribution();

            try
            {
                var options = butrContributionStore.Load();

                if (!options.Enabled)
                {
                    ButrContributionStatusMessage = Strings.Current["Settings.ButrContribution.OffNote"];
                    return;
                }

                var built = await Task.Run(BuildNewestCrashSubmission);

                if (built.Submission is null)
                {
                    ButrContributionStatusMessage = built.Reason;
                    return;
                }

                var payload = butrContribution.Prepare(built.Submission, options);

                ButrContributionStatusMessage = payload.Reason;

                if (!payload.IsReady)
                    return;

                preparedContribution = payload;
                ButrContributionPayload = payload.Json;
                ButrContributionReady = true;
                ButrContributionStatusMessage =
                    Strings.Current.Format("Settings.ButrContribution.ReadyNote", built.Reason, payload.Reason);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LoggingService.LogException(ex, "Failed to prepare a BUTR crash contribution");
                ButrContributionStatusMessage = Strings.Current["Settings.ButrContribution.PrepareFailed"];
            }
            finally
            {
                ButrContributionBusy = false;
            }
        }

        [RelayCommand(CanExecute = nameof(CanSendButrContribution))]
        private async Task SendButrContributionAsync()
        {
            if (preparedContribution is not { } payload)
                return;

            contributionCancellation?.Cancel();
            contributionCancellation?.Dispose();
            contributionCancellation = new CancellationTokenSource();

            ButrContributionBusy = true;

            try
            {
                var result = await butrContribution.SendAsync(
                    payload,
                    butrContributionStore.Load(),
                    DateTimeOffset.UtcNow,
                    contributionCancellation.Token);

                ButrContributionStatusMessage = result.Message;

                if (result.WasSent)
                    ClearPreparedContribution();
            }
            catch (OperationCanceledException)
            {
                ButrContributionStatusMessage = Strings.Current["Settings.ButrContribution.SendCanceled"];
            }
            finally
            {
                ButrContributionBusy = false;
            }
        }

        private bool CanSendButrContribution() => ButrContributionReady;

        [RelayCommand]
        private void CancelButrContribution() => contributionCancellation?.Cancel();

        // A prepared report is on screen until something takes it off again, and turning the whole
        // feature off to get rid of one report is not a way to get rid of one report.
        [RelayCommand(CanExecute = nameof(CanSendButrContribution))]
        private void DiscardButrContribution()
        {
            ClearPreparedContribution();
            ButrContributionStatusMessage = Strings.Current["Settings.ButrContribution.Discarded"];
        }

        private void ClearPreparedContribution()
        {
            preparedContribution = null;
            ButrContributionPayload = string.Empty;
            ButrContributionReady = false;
        }

        // The newest crash BEM can both read and attribute. Deliberately the newest one only: a
        // sweep of every report on disk would contribute the same crash under many names and count
        // one bad afternoon several times over.
        private static (ButrCrashSubmission? Submission, string Reason) BuildNewestCrashSubmission()
        {
            var install = InstallPath();

            if (string.IsNullOrWhiteSpace(install))
                return (null, Strings.Current["Settings.ButrContribution.NoInstall"]);

            var modules = InstalledModules();

            if (modules.Count == 0)
                return (null, Strings.Current["Settings.ButrContribution.NoModules"]);

            // The selected version's crash folders, not the resting version's: a report filed from
            // here has to be about the game the user is actually running.
            var search = CrashReportLocator.Find(install, Views.ShellViewModels.Instance.Environment.ActiveDataRoot);

            if (search.Reports.Count == 0)
                return (null, Strings.Current["Settings.ButrContribution.NoReports"]);

            var newest = search.Reports[0];
            var gameVersion = GameVersionReader.Read(install) is { IsEmpty: false } version ? version.ToString() : null;
            var ids = modules.Select(module => module.Id).ToList();
            var registries = new PatchRegistryStore(
                PatchRegistryStore.GetDefaultRoot(Views.ShellViewModels.Instance.Environment.ActiveDataRoot));
            var lookup = CrashAnalysis.Look(registries, ids, gameVersion);

            var attribution = CrashAnalysis.Analyze(
                newest.Report, AssemblyIndex.Build(install), ids, lookup, gameVersion, newest.Occurrences);

            var involved = attribution.Suspects
                .Select(suspect => new ButrCrashInvolvement(suspect.ModuleId.Value, suspect.Evidence))
                .ToList();

            var root = newest.Report.Root;

            var exception = new ButrCrashException(
                root?.TypeFullName ?? string.Empty,
                root?.Message ?? string.Empty,
                string.Join('\n', root?.Frames.Select(frame => "   at " + frame.Text.Trim()) ?? []));

            return (
                new ButrCrashSubmission(
                    gameVersion ?? string.Empty,
                    exception,
                    [.. modules.Select(module => new ButrCrashModule(
                        module.Id.Value,
                        module.VersionText ?? module.Version.ToString(),
                        module.Name,
                        module.IsOfficial,
                        module.Source == ModuleSource.Workshop,
                        module.Category is ModuleCategory.Singleplayer or ModuleCategory.Both,
                        module.Category is ModuleCategory.Multiplayer or ModuleCategory.Both,
                        module.Url))],
                    involved,
                    involved.Count == 1 ? involved[0].ModuleId : null,
                    Runtime: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    OperatingSystemVersion: Environment.OSVersion.Version.ToString()),
                Strings.Current.Format(
                    "Settings.ButrContribution.ReportRead", newest.Written.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
        }

        private static string? GameVersionText(IReadOnlyList<ModuleManifest> modules) =>
            modules.FirstOrDefault(module => module.Id.Value.Equals("Native", StringComparison.OrdinalIgnoreCase))
                is { } native
                ? native.VersionText ?? native.Version.ToString()
                : null;

        partial void OnNxmHandlerEnabledChanged(bool value)
        {
            if (loading)
                return;

            var handler = NxmLinks.Handler;
            handler.SaveEnabled(value);

            NxmStatus = value ? handler.Register().Message : handler.Unregister().Message;
            RefreshNxmStatus();
        }

        [RelayCommand]
        private void RegisterNxmLinks()
        {
            NxmStatus = NxmLinks.Handler.Register().Message;
            RefreshNxmStatus();
        }

        [RelayCommand]
        private void UnregisterNxmLinks()
        {
            NxmStatus = NxmLinks.Handler.Unregister().Message;
            RefreshNxmStatus();
        }

        private void RefreshNxmStatus()
        {
            var settings = NxmLinks.Handler.Settings;

            loading = true;
            NxmHandlerEnabled = settings.HandlerEnabled;
            loading = false;

            NxmPreviousHandler = settings.PreviousCommand is { } previous
                ? Strings.Current.Format("Settings.Nxm.PreviousHandler.Some", previous)
                : Strings.Current["Settings.Nxm.PreviousHandler.None"];

            NxmArchivesFolder = NxmLinks.ArchivesFolder() is { } archives
                ? Strings.Current.Format("Settings.Nxm.ArchivesFolder.Some", archives)
                : Strings.Current["Settings.Nxm.ArchivesFolder.None"];
        }

        partial void OnNexusUpdateCheckEnabledChanged(bool value)
        {
            _ = value;
            SaveNexusOptions();
        }

        partial void OnKeepDividersAnchoredOnAutoSortChanged(bool value) =>
            dividerOptionsStore.Save(new LoadOrderDividerOptions(value));

        partial void OnInstallSevenZipOnDemandChanged(bool value)
        {
            if (loading)
                return;

            toolkitOptionsStore.Save(new ToolkitOptions(value));
        }

        // Turning it on seeds the shared set from the instance the user is on, because the first
        // enable has nothing to project yet. Turning it off puts every instance back at once rather
        // than at each one's next launch, since "off" means each instance is using its own file now.
        //
        // Both directions rewrite the canonical game folders, and while a launch is live those are
        // junctions into whichever instance is running. So a running game refuses the change and the
        // box goes back, the same refusal the Versions page gives for a resting change.
        partial void OnShareGameSettingsChanged(bool value)
        {
            if (loading || revertingShareGameSettings)
                return;

            if (RunningGame.AnyGameProcessRunning())
            {
                SharedGameSettingsStatusMessage = Strings.Current["Core.GameSettings.GameRunning"];
                RevertShareGameSettings(!value);
                return;
            }

            try
            {
                var settings = instanceSettingsStore.Read();
                instanceSettingsStore.Write(settings with { ShareGameSettings = value });

                SharedGameSettingsStatusMessage = value ? SeedSharedGameSettings() : RestoreEveryInstance();
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Changing the shared game settings toggle");
                SharedGameSettingsStatusMessage = ex.Message;
            }
        }

        private void RevertShareGameSettings(bool to)
        {
            revertingShareGameSettings = true;

            try
            {
                ShareGameSettings = to;
            }
            finally
            {
                revertingShareGameSettings = false;
            }
        }

        private string SeedSharedGameSettings()
        {
            var manager = new InstanceManager(CanonicalPathSet.ForMachine(), instanceSettingsStore);

            if (manager.Active() is not { } active)
                return Strings.Current["Core.GameSettings.NothingWasShared"];

            var result = gameSettingsSync.Seed(GameSettingsTarget.For(active, manager.RestingInstanceId));

            return result.Failures.FirstOrDefault() is { } failure
                ? Strings.Current.Format(
                    "Core.GameSettings.CaptureFailed",
                    GameSettingsFiles.NameOf(failure.Kind),
                    failure.Error ?? string.Empty)
                : Strings.Current.Format("Core.GameSettings.SeededFrom", active.Record.DisplayName);
        }

        private string RestoreEveryInstance()
        {
            var manager = new InstanceManager(CanonicalPathSet.ForMachine(), instanceSettingsStore);
            var resting = manager.RestingInstanceId;
            var report = gameSettingsSync.RestoreAll(
                manager.List().Select(instance => GameSettingsTarget.For(instance, resting)));

            if (report.Failures.FirstOrDefault() is { } failure)
            {
                var file = failure.Result.Failures.FirstOrDefault();

                return Strings.Current.Format(
                    "Core.GameSettings.RestoreFailed",
                    Path.GetFileName(failure.InstanceFolder),
                    file?.Error ?? string.Empty);
            }

            return report.Restored == 0
                ? Strings.Current["Core.GameSettings.NothingWasShared"]
                : Strings.Current.Plural("Core.GameSettings.RestoredInstances", report.Restored, report.Restored);
        }

        partial void OnNexusPeriodIndexChanged(int value)
        {
            _ = value;
            SaveNexusOptions();
        }

        private void SaveNexusOptions()
        {
            if (loading)
                return;

            nexusOptionsStore.Save(new NexusOptions(
                NexusUpdateCheckEnabled,
                (NexusUpdatePeriod)Math.Clamp(NexusPeriodIndex, 0, 2),
                nexusOptionsStore.Load().CacheHours,
                BemUpdateCheckAtStartup));
        }

        [RelayCommand]
        private void OpenNexusAcceptableUsePolicy() =>
            OpenUrl(NexusEndpoints.AcceptableUsePolicyUrl, Strings.Current["Settings.Nexus.AcceptableUsePolicyLabel"]);

        // The only way a key reaches BEM: Nexus grants single sign-on only to an application that does
        // not also take personal API keys.
        [RelayCommand]
        private async Task SignInWithNexusAsync()
        {
            ssoCancellation?.Cancel();
            ssoCancellation?.Dispose();
            ssoCancellation = new CancellationTokenSource();

            NexusSsoBusy = true;

            try
            {
                var result = await ssoSession.RunAsync(
                    NexusSsoRequest.Default,
                    new Progress<string>(line => NexusSsoMessage = line),
                    ssoCancellation.Token);

                NexusSsoMessage = result.Message;
                RefreshKeyStatus();
            }
            finally
            {
                NexusSsoBusy = false;
            }
        }

        [RelayCommand]
        private void CancelNexusSignIn() => ssoCancellation?.Cancel();

        [RelayCommand]
        private void RemoveApiKey()
        {
            NexusStatusMessage = keyStore.Clear().Message;
            RefreshKeyStatus();
        }

        [RelayCommand]
        private async Task ValidateApiKeyAsync()
        {
            if (keyStore.Load() is not { } key)
            {
                NexusStatusMessage = Strings.Current["Settings.ValidateApiKey.NoKey"];
                return;
            }

            using var cancellation = new CancellationTokenSource();
            NexusBusy = true;

            try
            {
                var result = await new NexusClient(new NexusHttpTransport()).ValidateAsync(key, cancellation.Token);

                NexusStatusMessage = result.Message;
                ShowQuota(result.RateLimit);
            }
            finally
            {
                NexusBusy = false;
            }
        }

        // BEM checking on itself, against its own Nexus page, through the public API that needs no key.
        // All it can honestly offer is "a newer BEM exists, here is its page" - BEM is not a module
        // and nothing can install it through the pipeline - so that is all it offers. The answer is
        // kept, so the startup check does not ask again the same day.
        [RelayCommand]
        private async Task CheckBemUpdateAsync()
        {
            NexusBusy = true;

            try
            {
                var result = await Core.Nexus.BemUpdateCheck.FetchPublishedAsync(
                    new NexusHttpTransport(), CancellationToken.None);

                if (!result.Found)
                {
                    BemUpdateStatus = result.TransportError is { } error
                        ? Strings.Current.Format("Settings.BemUpdate.Unreachable", error)
                        : Strings.Current.Format("Settings.BemUpdate.NoAnswer", result.StatusCode);
                    return;
                }

                var stateStore = new Core.Nexus.BemUpdateStateStore(
                    Path.Combine(NexusApiKeyStore.DefaultDirectory, Core.Nexus.BemUpdateStateStore.FileName));
                stateStore.Save(Core.Nexus.BemUpdateNotice.Checked(stateStore.Load(), result.Version!, DateTimeOffset.UtcNow));

                var installed = Services.AppVersion.Display;
                var published = result.Version;

                BemUpdateStatus = Core.Nexus.BemUpdateCheck.Compare(installed, published) switch
                {
                    Core.Nexus.BemUpdateVerdict.NewerAvailable =>
                        Strings.Current.Format("Settings.BemUpdate.NewerAvailable", published, installed),
                    Core.Nexus.BemUpdateVerdict.UpToDate =>
                        Strings.Current.Format("Settings.BemUpdate.UpToDate", installed, published),
                    _ => Strings.Current.Format("Settings.BemUpdate.Unrecognized", published, installed)
                };
            }
            finally
            {
                NexusBusy = false;
            }
        }

        [RelayCommand]
        private static async Task OpenBemPageAsync() =>
            _ = await Windows.System.Launcher.LaunchUriAsync(new Uri(Core.Nexus.BemUpdateCheck.PageUrl));

        [ObservableProperty]
        public partial string BemUpdateStatus { get; set; } = string.Empty;

        [RelayCommand]
        private async Task CheckNexusUpdatesAsync()
        {
            nexusCancellation?.Cancel();
            nexusCancellation?.Dispose();
            nexusCancellation = new CancellationTokenSource();

            NexusBusy = true;

            try
            {
                var options = nexusOptionsStore.Load();

                var report = await updateCheck.RunAsync(
                    new NexusUpdateRequest(
                        NexusModuleMatching.Link(
                            InstalledModules(),
                            recorded: ModuleArchiveLinkStore.For(
                                Views.ShellViewModels.Instance.Environment.ActiveDataRoot)),
                        keyStore.Load(),
                        options.UpdateCheckEnabled,
                        options.Period,
                        options.CacheLifetime,
                        DateTimeOffset.UtcNow,
                        ForceRefresh: true),
                    nexusCancellation.Token);

                NexusUpdates.Clear();

                foreach (var update in report.Updates)
                    NexusUpdates.Add(new NexusUpdateRow(update));

                NexusResultLines.Clear();

                foreach (var line in Sentences(report.Message))
                    NexusResultLines.Add(line);

                NexusDataFetched = report.DataFetchedUtc is { } fetched
                    ? fetched.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : string.Empty;

                // The findings are the message now, so a leftover one-liner beside them would read as
                // a second, contradicting answer.
                NexusStatusMessage = string.Empty;

                ShowQuota(report.RateLimit);
            }
            catch (OperationCanceledException)
            {
                NexusStatusMessage = Strings.Current["Settings.NexusCheck.Canceled"];
            }
            finally
            {
                NexusBusy = false;
            }
        }

        [RelayCommand]
        private void CancelNexusCheck() => nexusCancellation?.Cancel();

        // The report arrives as one string of whole sentences joined by spaces, and it is built in
        // Core where other screens read it as prose. Splitting it here rather than rewording it there
        // keeps every word exactly as the check wrote it while giving each fact its own line.
        private static IEnumerable<string> Sentences(string message) =>
            message
                .Split(". ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(sentence => sentence.EndsWith('.') ? sentence : sentence + ".");

        // Read off the record's own figures rather than its one-line summary, so the hourly and daily
        // numbers are separate rows a reader can find at a glance and copy one at a time.
        private void ShowQuota(NexusRateLimit limit)
        {
            if (!limit.IsKnown)
            {
                NexusQuotaHourly = string.Empty;
                NexusQuotaDaily = string.Empty;
                NexusQuotaNote = Strings.Current["Settings.NexusQuota.Unknown"];
                return;
            }

            NexusQuotaHourly = Remaining(limit.HourlyRemaining, limit.HourlyLimit);
            NexusQuotaDaily = Remaining(limit.DailyRemaining, limit.DailyLimit);
            NexusQuotaNote = Strings.Current["Settings.NexusQuota.Note"];
        }

        // The noun ("request(s)") agrees with the total the count is drawn out of, not with how many are
        // left: "1 of 100 requests left" is still plural because the pool is 100. When the limit itself
        // is not known, there is no total to grammar off, so this falls back to the remaining count -
        // the same thing the sentence showed before this had a Plural form at all.
        private static string Remaining(int? remaining, int? limit) =>
            remaining is { } left
                ? limit is { } limitValue
                    ? Strings.Current.Plural("Settings.NexusQuota.Remaining", limitValue, left)
                    : Strings.Current.Plural(
                        "Settings.NexusQuota.RemainingUnstatedLimit", left, Strings.Current["Settings.NexusQuota.UnstatedLimit"])
                : Strings.Current["Settings.NexusQuota.RemainingUnknown"];

        private void RefreshKeyStatus()
        {
            var status = keyStore.Status();

            HasNexusKey = status.State == NexusApiKeyState.Available;
            NexusKeyStatus = status.Message;
        }

        // Scanned here rather than read off the load order page, so this works whether or not that
        // page has been opened, and so nothing on it is disturbed.
        private static IReadOnlyList<ModuleManifest> InstalledModules()
        {
            var path = InstallPath();

            if (string.IsNullOrWhiteSpace(path))
                return [];

            try
            {
                return ModuleScanner.ScanAll(path).Modules;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to scan modules for the Nexus update check");
                return [];
            }
        }

        // Crash Reports is where the contribution is offered, and a user can reach that page without
        // ever opening the load order or the install page, so the folder it scanned is asked first.
        private static string InstallPath() =>
            FirstNonEmpty(
                ShellViewModels.Instance.Diagnostics.GameInstallPath,
                ShellViewModels.Instance.Environment.GameInstallPath,
                ShellViewModels.Instance.Install.GameInstallPath,
                Environment.GetEnvironmentVariable("BANNERLORD_GAME_DIR"));

        private static string FirstNonEmpty(params string?[] candidates) =>
            candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate)) ?? string.Empty;

        private void OpenUrl(string url, string what)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                NexusStatusMessage = Strings.Current.Format("Settings.OpenUrl.Success", what);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                LoggingService.LogException(ex, $"Failed to open {url}");
                NexusStatusMessage = Strings.Current.Format("Settings.OpenUrl.Failure", what, url);
            }
        }
    }
}
