using System.Collections.ObjectModel;
using System.Diagnostics;
using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Nexus;
using BannerlordEnvironmentManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace BannerlordEnvironmentManager.ViewModels
{
    // A Nexus collection, installed the way the BUTR stack is: the revision's pinned files are read with no
    // key, planned against every ticked instance, then fetched unattended for a Premium account or walked
    // one Mod Manager Download at a time for a free one. Reading comes first and installs nothing, so the
    // player sees what the collection would change before anything is written.
    public partial class InstallViewModel
    {
        [ObservableProperty]
        public partial bool IsCollectionVisible { get; set; }

        partial void OnIsCollectionVisibleChanged(bool value)
        {
            if (!value)
                StopCollectionRun();
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCollectionClosable))]
        [NotifyPropertyChangedFor(nameof(IsCollectionIdle))]
        [NotifyPropertyChangedFor(nameof(CanAcceptCollection))]
        public partial bool IsCollectionRunning { get; set; }

        public bool IsCollectionClosable => !IsCollectionRunning;

        public bool IsCollectionIdle => !IsCollectionRunning;

        [ObservableProperty]
        public partial bool IsCollectionGuided { get; set; }

        [ObservableProperty]
        public partial string CollectionTitle { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string CollectionMessage { get; set; } = string.Empty;

        partial void OnCollectionMessageChanged(string value)
        {
            HideAcceptedCollection();
            if (IsCollectionRunning)
                StatusMessage = value;
        }

        [ObservableProperty]
        public partial InfoBarSeverity CollectionSeverity { get; set; } = InfoBarSeverity.Informational;

        // Accept the risk: a warning or error this bar showed and the user accepted stays hidden while
        // its words are the same. AcceptedNotices holds the record, and Settings lists it to forgive.
        public bool CanAcceptCollection => AcceptedNotices.IsWarning(CollectionSeverity) && !IsCollectionRunning;

        partial void OnCollectionSeverityChanged(InfoBarSeverity value) => HideAcceptedCollection();

        private void HideAcceptedCollection()
        {
            OnPropertyChanged(nameof(CanAcceptCollection));
            if (!IsCollectionRunning && AcceptedNotices.IsAccepted("Collection", CollectionSeverity, CollectionTitle, CollectionMessage))
                IsCollectionVisible = false;
        }

        [RelayCommand]
        private void AcceptCollection()
        {
            AcceptedNotices.Accept("Collection", AcceptedNotices.Identity(CollectionTitle, CollectionMessage));
            IsCollectionVisible = false;
        }

        [ObservableProperty]
        public partial string CollectionLinkText { get; set; } = string.Empty;

        partial void OnCollectionLinkTextChanged(string value)
        {
            collectionPlans = [];
            bemCollection = null;
            OnPropertyChanged(nameof(HasCollectionPlan));
            RefreshCollectionReadiness();
        }

        // The curator's optional marks are kept, and whether they are fetched is the player's, saved
        // with the rest of this page's settings.
        [ObservableProperty]
        public partial bool IncludeOptionalCollectionMods { get; set; }

        // The read's summary counts what would be fetched under the old choice, so the plan is read again.
        partial void OnIncludeOptionalCollectionModsChanged(bool value) => OnCollectionLinkTextChanged(CollectionLinkText);

        // On, an entry the curator set to follow updates is planned on the newest current file of its
        // lineage, as Vortex installs it; off, every entry stays on the file the revision pins.
        [ObservableProperty]
        public partial bool FollowCuratorUpdateChoice { get; set; } = true;

        // A plan read under the other choice names other files, so it is dropped for a fresh read.
        partial void OnFollowCuratorUpdateChoiceChanged(bool value) => OnCollectionLinkTextChanged(CollectionLinkText);

        public ObservableCollection<string> CollectionResults { get; } = [];

        // One plan per ticked instance, read when Read Collection was pressed and dropped whenever the
        // link changes, so Install Collection always acts on the plan the player has just looked at.
        private IReadOnlyList<(InstallTarget Destination, NexusCollectionPlan Plan)> collectionPlans = [];

        public bool HasCollectionPlan => collectionPlans.Count > 0;

        private IReadOnlyDictionary<int, IReadOnlyList<InstallTarget>> collectionDestinations =
            new Dictionary<int, IReadOnlyList<InstallTarget>>();

        private NexusCollectionFlow? collectionFlow;

        // Ended on the way out for the reason ButrStackWalk is: an ended walk answers no to IsWaitingFor,
        // which is what keeps it from skipping a later download's destination confirmation.
        private NexusCollectionFlow? CollectionWalk
        {
            get => collectionFlow;
            set
            {
                if (ReferenceEquals(collectionFlow, value))
                    return;

                collectionFlow?.End(Strings.Current["Install.Collection.NotReached"]);
                collectionFlow = value;
            }
        }

        private CancellationTokenSource? collectionCts;

        [RelayCommand]
        private void ShowCollection()
        {
            if (!IsCollectionVisible)
                ShowCollectionBar(InfoBarSeverity.Informational, Strings.Current["Install.Collection.Title"],
                    Strings.Current["Install.Collection.Prompt"]);

            IsCollectionVisible = true;
            RefreshCollectionReadiness();
        }

        public void RefreshCollectionReadiness()
        {
            ReadCollectionCommand.NotifyCanExecuteChanged();
            InstallCollectionCommand.NotifyCanExecuteChanged();
            SkipCollectionModCommand.NotifyCanExecuteChanged();
        }

        private string CollectionBlockedBecause()
        {
            if (IsCollectionRunning)
                return Strings.Current["Install.Collection.AlreadyRunning"];

            if (IsBusy)
                return Strings.Current["Install.ButrStack.Busy"];

            var chosen = ChosenDestinations();

            if (chosen.Count == 0)
                return Strings.Current["Install.Destinations.NoneSelected"];

            return chosen.Any(destination => Directory.Exists(destination.GameFolder))
                ? string.Empty
                : Strings.Current["Install.ButrStack.NoDestination"];
        }

        private bool CanReadCollection() =>
            CollectionBlockedBecause().Length == 0
            && (NexusCollectionRef.TryParse(CollectionLinkText) is not null || CollectionFileIn(CollectionLinkText) is not null);

        [RelayCommand(CanExecute = nameof(CanReadCollection))]
        private async Task ReadCollectionAsync()
        {
            if (CollectionFileIn(CollectionLinkText) is { } collectionFile)
            {
                await ReadCollectionFileAsync(collectionFile);
                return;
            }

            if (NexusCollectionRef.TryParse(CollectionLinkText) is not { } reference)
            {
                ShowCollectionBar(InfoBarSeverity.Warning, Strings.Current["Install.Collection.Title"],
                    Strings.Current["Install.Collection.NotALink"]);
                return;
            }

            ResolveActiveInstanceInstallTarget();
            RefreshInstallDestinations();
            CollectionResults.Clear();
            collectionPlans = [];
            bemCollection = null;
            nexusBundleFolder = null;
            OnPropertyChanged(nameof(HasCollectionPlan));
            ShowCollectionBar(InfoBarSeverity.Informational, Strings.Current["Install.Collection.Title"],
                Strings.Current.Format("Install.Collection.Reading", reference.Slug));

            IsBusy = true;

            try
            {
                var fetch = await NexusCollections.FetchAsync(new NexusHttpTransport(), reference, CancellationToken.None);

                if (fetch.Revision is not { } pinned)
                {
                    ShowCollectionBar(InfoBarSeverity.Warning, Strings.Current["Install.Collection.Title"],
                        CollectionFailureMessage(fetch));
                    return;
                }

                var resolution = await NexusCollectionFiles.ApplyAsync(
                    new NexusHttpTransport(), pinned, FollowCuratorUpdateChoice, CancellationToken.None);
                var revision = resolution.Revision;

                var destinations = ChosenDestinations();
                var plans = new List<(InstallTarget, NexusCollectionPlan)>();

                foreach (var destination in destinations)
                {
                    var scan = await Task.Run(() => ModuleScanner.ScanAll(destination.GameFolder));

                    var links = NexusModuleMatching.Link(
                        scan.Modules.Where(module => !module.IsOfficial),
                        recorded: ArchiveLinks,
                        confirmed: confirmedNexusIds);

                    plans.Add((destination, NexusCollectionPlanner.Plan(revision, links)));
                }

                collectionPlans = plans;
                bemCollection = await ReadNexusBundleAsync(pinned);
                OnPropertyChanged(nameof(HasCollectionPlan));

                foreach (var (destination, plan) in plans)
                {
                    foreach (var item in plan.Items)
                        CollectionResults.Add(CollectionResultLine(item.Mod.ModName, destination, StepText(item), destinations.Count));
                }

                ShowCollectionBar(InfoBarSeverity.Informational,
                    Strings.Current.Format("Install.Collection.ReadTitle", revision.Name, revision.RevisionNumber),
                    DescribePlans(revision, resolution.Unread)
                        + (bemCollection is { Settings.Count: > 0 } bundled
                            ? " " + Strings.Current.Format("Install.Collection.FileApplies", 0, bundled.Settings.Count)
                            : string.Empty));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LoggingService.LogException(ex, "Reading a Nexus collection failed");
                ShowCollectionBar(InfoBarSeverity.Error, Strings.Current["Install.Collection.Title"],
                    Strings.Current.Format("Install.Collection.Failed", ex.Message));
            }
            finally
            {
                IsBusy = false;
                StatusMessage = string.Empty;
                RefreshCollectionReadiness();
            }
        }

        private static string CollectionFailureMessage(NexusCollectionFetch fetch) => fetch.Failure switch
        {
            NexusCollectionFailure.NotFound => Strings.Current["Install.Collection.NotFound"],
            NexusCollectionFailure.NoAnswer => Strings.Current.Format("Install.Collection.NoAnswer", fetch.Detail ?? string.Empty),
            NexusCollectionFailure.Refused => Strings.Current.Format("Install.Collection.Refused",
                fetch.Detail ?? fetch.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            _ => Strings.Current["Install.Collection.Unreadable"]
        };

        private string DescribePlans(NexusCollectionRevision revision, NexusModFilesFetch? unread)
        {
            var fetches = CollectionFetches().Count;
            var summary = Strings.Current.Format("Install.Collection.Summary",
                revision.Mods.Count,
                fetches,
                collectionPlans.Sum(entry => entry.Plan.Count(NexusCollectionStep.AlreadyInstalled)),
                collectionPlans.Sum(entry => entry.Plan.Count(NexusCollectionStep.LeftAlone)));

            if (collectionPlans[0].Plan.OptionalCount > 0)
                summary += " " + Strings.Current.Format("Install.Collection.OptionalCount", collectionPlans[0].Plan.OptionalCount);

            // An entry Nexus lists without a file behind it (a file taken down since) cannot be fetched by
            // anyone, so it is named rather than left for the player to find missing in game.
            if (revision.Unlisted > 0)
                summary += " " + Strings.Current.Format("Install.Collection.Unlisted", revision.Unlisted);

            if (unread is not null)
                summary += " " + Strings.Current.Format("Install.Collection.FilesUnread",
                    unread.Detail ?? unread.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture));

            return summary;
        }

        private static string StepText(NexusCollectionItem item) =>
            item.Mod.ResolvedFrom is { } pinned && item.Step is NexusCollectionStep.Install or NexusCollectionStep.Update
                ? Strings.Current.Format("Install.Collection.ResolvedNote", PlannedStepText(item),
                    item.Mod.Version ?? item.Mod.FileName ?? string.Empty,
                    pinned.Version ?? pinned.FileName ?? string.Empty)
                : PlannedStepText(item);

        private static string PlannedStepText(NexusCollectionItem item) => item.Step switch
        {
            NexusCollectionStep.Install => item.Mod.Optional
                ? Strings.Current["Install.Collection.Step.InstallOptional"]
                : Strings.Current["Install.Collection.Step.Install"],
            NexusCollectionStep.Update => Strings.Current.Format(
                item.Mod.Optional ? "Install.Collection.Step.UpdateOptional" : "Install.Collection.Step.Update",
                item.InstalledVersion ?? string.Empty, item.Mod.Version ?? string.Empty),
            NexusCollectionStep.AlreadyInstalled => Strings.Current["Install.Collection.Step.AlreadyInstalled"],
            _ => Strings.Current.Format("Install.Collection.Step.LeftAlone",
                item.InstalledVersion ?? string.Empty, item.Mod.Version ?? string.Empty)
        };

        private static string CollectionResultLine(string name, InstallTarget destination, string reason, int destinationCount) =>
            destinationCount > 1
                ? Strings.Current.Format("Install.ButrStack.ResultLineForInstance", name, destination.DisplayName, reason)
                : Strings.Current.Format("Install.ButrStack.ResultLine", name, reason);

        // One entry per pinned file any instance needs, taken from the first instance that needs it, with
        // the instances behind each kept so a download that arrives later is written into exactly those.
        private List<NexusCollectionItem> CollectionFetches()
        {
            collectionDestinations = collectionPlans
                .SelectMany(entry => entry.Plan.Fetches(IncludeOptionalCollectionMods).Select(item => (item.Mod.FileId, entry.Destination)))
                .GroupBy(pair => pair.FileId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<InstallTarget>)[.. group.Select(pair => pair.Destination)]);

            return [.. collectionPlans
                .SelectMany(entry => entry.Plan.Fetches(IncludeOptionalCollectionMods))
                .GroupBy(item => item.Mod.FileId)
                .Select(group => group.First())];
        }

        private IReadOnlyList<InstallTarget> CollectionDestinationsFor(NexusCollectionItem item) =>
            collectionDestinations.TryGetValue(item.Mod.FileId, out var chosen) ? chosen : ChosenDestinations();

        private bool CanInstallCollection() => HasCollectionPlan && CollectionBlockedBecause().Length == 0;

        [RelayCommand(CanExecute = nameof(CanInstallCollection))]
        private async Task InstallCollectionAsync()
        {
            var fetches = CollectionFetches();

            if (fetches.Count == 0)
            {
                var nothing = Strings.Current["Install.Collection.NothingToFetch"];

                if (bemCollection is { } toApply)
                {
                    IsBusy = true;

                    try
                    {
                        nothing += " " + await ApplyBemCollectionAsync(toApply);
                    }
                    finally
                    {
                        IsBusy = false;
                    }
                }

                ShowCollectionBar(InfoBarSeverity.Success, Strings.Current["Install.Collection.Title"], nothing);
                return;
            }

            collectionCts?.Dispose();
            collectionCts = new CancellationTokenSource();
            CollectionWalk = null;
            CollectionResults.Clear();
            ClearInstallResults();
            IsCollectionGuided = false;
            IsCollectionRunning = true;
            IsBusy = true;
            RefreshCollectionReadiness();
            ShowCollectionBar(InfoBarSeverity.Informational, Strings.Current["Install.Collection.Title"],
                Strings.Current["Install.ButrStack.CheckingAccount"]);

            try
            {
                var apiKey = new NexusApiKeyStore(NexusApiKeyStore.DefaultDirectory, new WindowsSecretProtector()).Load();

                if (apiKey is not { } key)
                {
                    IsNexusKeyNeeded = true;
                    FinishCollection(InfoBarSeverity.Warning, Strings.Current["Install.ButrStack.NoApiKey"]);
                    return;
                }

                var account = await new NexusClient(new NexusHttpTransport()).ValidateAsync(key, collectionCts.Token);

                if (!account.IsOk || account.Value is not { } user)
                {
                    IsNexusKeyNeeded = account.Outcome is NexusOutcome.Unauthorized;
                    FinishCollection(InfoBarSeverity.Warning,
                        Strings.Current.Format("Install.ButrStack.AccountUnreadable", account.Message));
                    return;
                }

                if (user.IsPremium)
                    await InstallCollectionDirectlyAsync(fetches, collectionCts.Token);
                else
                    StartGuidedCollection(fetches);
            }
            catch (OperationCanceledException)
            {
                FinishCollection(InfoBarSeverity.Informational, Strings.Current["Install.Collection.Canceled"]);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LoggingService.LogException(ex, "The collection install failed");
                FinishCollection(InfoBarSeverity.Error, Strings.Current.Format("Install.Collection.Failed", ex.Message));
            }
            finally
            {
                if (!IsCollectionRunning)
                    IsBusy = false;
            }
        }

        private async Task InstallCollectionDirectlyAsync(IReadOnlyList<NexusCollectionItem> fetches, CancellationToken cancellationToken)
        {
            CollectionMessage = Strings.Current.Plural("Install.ButrStack.Installing", fetches.Count);

            var results = new List<ModUpdateInstallResult>();

            foreach (var group in fetches.GroupBy(
                item => string.Join('\n', CollectionDestinationsFor(item).Select(destination => destination.InstanceId)),
                StringComparer.Ordinal))
            {
                results.AddRange(await InstallModUpdatesAsync(
                    [.. group.Select(item => item.Target)],
                    new Progress<string>(message => CollectionMessage = message),
                    cancellationToken,
                    CollectionDestinationsFor(group.First())));
            }

            foreach (var result in results)
                CollectionResults.Add(Strings.Current.Format("Install.ButrStack.ResultLine", result.ModuleName, result.Message));

            var installed = results.Count(result => result.Installed);

            FinishCollection(
                installed == results.Count ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                Strings.Current.Format("Install.ButrStack.Done", installed, results.Count - installed));
        }

        private void StartGuidedCollection(IReadOnlyList<NexusCollectionItem> fetches)
        {
            CollectionWalk = new NexusCollectionFlow(fetches);
            IsCollectionGuided = true;
            RefreshCollectionReadiness();
            OpenCurrentCollectionPage();
        }

        private void OpenCurrentCollectionPage()
        {
            if (CollectionWalk is not { Current: { } item } flow)
            {
                CompleteGuidedCollection();
                return;
            }

            ShowCollectionBar(InfoBarSeverity.Informational,
                Strings.Current.Format("Install.Collection.WalkTitle", flow.Position, flow.Total, item.Mod.ModName),
                Strings.Current.Format("Install.ButrStack.ClickModManagerDownload", item.Mod.ModName));

            var url = ButrStack.ModManagerDownloadUrl(item.Mod.ModId, item.Mod.FileId);

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                LoggingService.LogException(ex, $"Could not open the Nexus page for {item.Mod.ModName}");
                CollectionMessage = Strings.Current.Format("Install.ButrStack.BrowserFailed", item.Mod.ModName, url);
            }
        }

        public bool IsCollectionWaitingFor(NxmLink link) =>
            link.ModId is { } modId && CollectionWalk?.IsWaitingFor(modId, link.FileId) == true;

        public async Task ContinueCollectionAsync(NxmLink link)
        {
            if (CollectionWalk is not { Current: { } step })
                return;

            // Fire and forget from the nxm handler, so nothing here may throw, for the reason given on
            // ContinueButrStackAsync.
            try
            {
                var download = await DownloadFromNexusAsync(link, collectionCts?.Token ?? CancellationToken.None);

                if (download.Status != NxmDownloadStatus.Downloaded || download.Path is not { Length: > 0 } archivePath)
                {
                    RecordCollectionStep(step, false, download.Message);
                    return;
                }

                CollectionMessage = Strings.Current.Format("Install.Update.Installing", step.Mod.ModName);

                var outcome = await InstallUpdateArchiveAsync(
                    archivePath, CollectionDestinationsFor(step), null, collectionCts?.Token ?? CancellationToken.None);

                RecordCollectionStep(step, outcome.Installed, outcome.Status);
            }
            catch (OperationCanceledException)
            {
                RecordCollectionStep(step, false, Strings.Current["Install.Collection.Canceled"]);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LoggingService.LogException(ex, $"The collection step for {step.Mod.ModName} failed");
                RecordCollectionStep(step, false, ex.Message);
            }
        }

        private void RecordCollectionStep(NexusCollectionItem step, bool installed, string message)
        {
            CollectionResults.Add(Strings.Current.Format("Install.ButrStack.ResultLine", step.Mod.ModName, message));

            if (CollectionWalk?.Record(step, installed, message) == true)
                OpenCurrentCollectionPage();
        }

        private bool CanSkipCollectionMod() => IsCollectionRunning && IsCollectionGuided;

        [RelayCommand(CanExecute = nameof(CanSkipCollectionMod))]
        private void SkipCollectionMod()
        {
            if (CollectionWalk is not { Current: { } item })
                return;

            var skipped = Strings.Current["Core.Nexus.ButrStack.Flow.Skipped"];

            CollectionResults.Add(Strings.Current.Format("Install.ButrStack.ResultLine", item.Mod.ModName, skipped));
            CollectionWalk.Skip(skipped);
            OpenCurrentCollectionPage();
        }

        [RelayCommand]
        private void CancelCollection() => StopCollectionRun();

        private void StopCollectionRun()
        {
            if (!IsCollectionRunning && CollectionWalk is null)
                return;

            collectionCts?.Cancel();

            if (CollectionWalk is { Finished: false } flow)
            {
                flow.End(Strings.Current["Install.Collection.NotReached"]);
                CompleteGuidedCollection();
                return;
            }

            FinishCollection(InfoBarSeverity.Informational, Strings.Current["Install.Collection.Canceled"]);
        }

        private void CompleteGuidedCollection()
        {
            var outcomes = CollectionWalk?.Outcomes ?? [];
            var installed = outcomes.Count(outcome => outcome.Installed);

            FinishCollection(
                outcomes.Count > 0 && installed == outcomes.Count ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                Strings.Current.Format("Install.ButrStack.Done", installed, outcomes.Count - installed));
        }

        private void ShowCollectionBar(InfoBarSeverity severity, string title, string message)
        {
            CollectionSeverity = severity;
            CollectionTitle = title;
            CollectionMessage = message;
        }

        // Whatever landed stays. Reading the collection again plans against what is on disk, so a walk
        // abandoned halfway is finished rather than started over.
        private void FinishCollection(InfoBarSeverity severity, string message)
        {
            // A finished run, even one with some mods skipped, still applies the curator's load order
            // and settings for the mods that did arrive. A cancelled, refused or never-started one
            // (no results recorded) applies nothing.
            if (bemCollection is { } toApply
                && severity is InfoBarSeverity.Success or InfoBarSeverity.Warning
                && CollectionResults.Count > 0)
            {
                _ = ApplyAfterRunAsync(toApply, severity, message);
                return;
            }

            CompleteCollection(severity, message);
        }

        private async Task ApplyAfterRunAsync(BemCollection collection, InfoBarSeverity severity, string message)
        {
            try
            {
                message += " " + await ApplyBemCollectionAsync(collection);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LoggingService.LogException(ex, "Applying a BEM collection's order and settings failed");
                message += " " + Strings.Current.Format("Install.Collection.ApplyFailed", ex.Message);
                severity = InfoBarSeverity.Warning;
            }

            CompleteCollection(severity, message);
        }

        private void CompleteCollection(InfoBarSeverity severity, string message)
        {
            CollectionWalk = null;
            IsCollectionRunning = false;
            IsCollectionGuided = false;
            IsBusy = false;
            collectionPlans = [];
            OnPropertyChanged(nameof(HasCollectionPlan));
            ShowCollectionBar(severity, Strings.Current["Install.Collection.Title"], message);
            RefreshCollectionReadiness();
        }
    }
}
