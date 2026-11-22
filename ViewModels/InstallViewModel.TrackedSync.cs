using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Nexus;
using BannerlordEnvironmentManager.Services;
using CommunityToolkit.Mvvm.Input;

namespace BannerlordEnvironmentManager.ViewModels
{
    // The Nexus tracked list beside the installed mod pages. Comparing reads it and says what differs;
    // tracking sends only the installed pages Nexus does not yet track, and nothing is ever untracked,
    // so the update notices Nexus sends keep covering what the player installed.
    public partial class InstallViewModel
    {
        private NexusTrackedSyncPlan? trackedPlan;

        [ObservableProperty]
        public partial string TrackedSyncStatus { get; set; } = string.Empty;

        public bool CanTrackMissing => trackedPlan is { ToTrack.Count: > 0 } && !IsBusy;

        [RelayCommand]
        private async Task CompareTrackedAsync()
        {
            if (IsBusy)
                return;

            IsBusy = true;

            try
            {
                var (key, links) = await TrackedInputsAsync();

                if (key is not { } apiKey)
                    return;

                var tracked = await new NexusClient(new NexusHttpTransport()).GetTrackedModsAsync(apiKey, CancellationToken.None);

                if (!tracked.IsOk || tracked.Value is not { } pages)
                {
                    IsNexusKeyNeeded = tracked.Outcome is NexusOutcome.Unauthorized;
                    TrackedSyncStatus = Strings.Current.Format("Install.Tracked.CompareFailed", tracked.Message);
                    return;
                }

                trackedPlan = NexusTrackedSync.Plan(pages, links);
                TrackedSyncStatus = trackedPlan.IsInSync
                    ? Strings.Current.Format("Install.Tracked.InSync", trackedPlan.InBoth, trackedPlan.NotInstalled.Count)
                    : Strings.Current.Format("Install.Tracked.Differs", trackedPlan.ToTrack.Count, trackedPlan.InBoth, trackedPlan.NotInstalled.Count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
            {
                LoggingService.LogException(ex, "Reading the Nexus tracked list failed");
                TrackedSyncStatus = Strings.Current.Format("Install.Tracked.CompareFailed", ex.Message);
            }
            finally
            {
                IsBusy = false;
                OnPropertyChanged(nameof(CanTrackMissing));
                TrackMissingCommand.NotifyCanExecuteChanged();
            }
        }

        [RelayCommand(CanExecute = nameof(CanTrackMissing))]
        private async Task TrackMissingAsync()
        {
            if (IsBusy || trackedPlan is not { ToTrack.Count: > 0 } plan)
                return;

            IsBusy = true;

            try
            {
                var (key, _) = await TrackedInputsAsync();

                if (key is not { } apiKey)
                    return;

                var (done, failure) = await NexusTrackedSync.ApplyAsync(
                    new NexusClient(new NexusHttpTransport()), apiKey, plan.ToTrack, CancellationToken.None);

                TrackedSyncStatus = failure is null
                    ? Strings.Current.Plural("Install.Tracked.Done", done.Count)
                    : Strings.Current.Format("Install.Tracked.Stopped", done.Count, plan.ToTrack.Count, failure.Message);

                IsNexusKeyNeeded = failure?.Outcome is NexusOutcome.Unauthorized;
                trackedPlan = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
            {
                LoggingService.LogException(ex, "Tracking mods on Nexus failed");
                TrackedSyncStatus = Strings.Current.Format("Install.Tracked.Stopped", 0, plan.ToTrack.Count, ex.Message);
            }
            finally
            {
                IsBusy = false;
                OnPropertyChanged(nameof(CanTrackMissing));
                TrackMissingCommand.NotifyCanExecuteChanged();
            }
        }

        // The stored key, or the line that says it is missing, and the Nexus page each installed module
        // links to in the version being managed.
        private async Task<(NexusApiKey? Key, IReadOnlyList<NexusModuleLink> Links)> TrackedInputsAsync()
        {
            var key = new NexusApiKeyStore(NexusApiKeyStore.DefaultDirectory, new WindowsSecretProtector()).Load();

            if (key is null)
            {
                IsNexusKeyNeeded = true;
                TrackedSyncStatus = Strings.Current["Install.Tracked.NoApiKey"];
                return (null, []);
            }

            ResolveActiveInstanceInstallTarget();
            RefreshInstallDestinations();

            var destination = ChosenDestinations().FirstOrDefault(target => Directory.Exists(target.GameFolder));

            if (destination is null)
            {
                TrackedSyncStatus = Strings.Current["Install.ButrStack.NoDestination"];
                return (null, []);
            }

            var links = await Task.Run(() => NexusModuleMatching.Link(
                ModuleScanner.ScanAll(destination.GameFolder).Modules.Where(module => !module.IsOfficial),
                recorded: ArchiveLinks,
                confirmed: confirmedNexusIds));

            return (key, [.. links]);
        }
    }
}
