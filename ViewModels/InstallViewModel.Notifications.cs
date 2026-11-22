using System.Collections.ObjectModel;
using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Nexus;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels
{
    public sealed class NotificationRowViewModel(NotificationEntry entry)
    {
        public NotificationEntry Entry { get; } = entry;

        public string Title => Entry.Title;

        public string Detail => string.IsNullOrWhiteSpace(Entry.Detail)
            ? Entry.WhenUtc.ToLocalTime().ToString("g")
            : $"{Entry.WhenUtc.ToLocalTime():g} - {Entry.Detail}";

        public bool IsUnread => !Entry.IsRead;
    }

    // The Library's notifications list: every download and update BEM finished, newest first, kept
    // across sessions so one that finished while the owner was away is still there.
    public partial class InstallViewModel
    {
        private const int NotificationRowLimit = 50;

        private readonly NotificationStore notifications = new(NotificationStore.DefaultPath());

        public ObservableCollection<NotificationRowViewModel> NotificationRows { get; } = [];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoNotifications))]
        public partial bool HasNotifications { get; set; }

        public bool HasNoNotifications => !HasNotifications;

        [ObservableProperty]
        public partial string NotificationSummary { get; set; } = string.Empty;

        public void RefreshNotifications()
        {
            try
            {
                var entries = notifications.Load();

                NotificationRows.Clear();

                foreach (var entry in entries.Take(NotificationRowLimit))
                    NotificationRows.Add(new NotificationRowViewModel(entry));

                HasNotifications = NotificationRows.Count > 0;

                var unread = entries.Count(entry => !entry.IsRead);

                NotificationSummary = unread == 0
                    ? string.Empty
                    : Strings.Current.Plural("Install.Notifications.Unread", unread);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to read the notifications");
            }
        }

        private void Notify(NotificationKind kind, string title, string detail)
        {
            try
            {
                notifications.Add(new NotificationEntry(DateTime.UtcNow, kind, title, detail));
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Failed to record a notification");
                return;
            }

            _ = OnPageThreadAsync(() =>
            {
                RefreshNotifications();
                return Task.CompletedTask;
            });
        }

        private void NotifyDownload(string name, NxmDownloadResult result)
        {
            if (result.Status == NxmDownloadStatus.Canceled)
                return;

            var worked = result.Status == NxmDownloadStatus.Downloaded;

            Notify(
                worked ? NotificationKind.DownloadFinished : NotificationKind.DownloadFailed,
                Strings.Current.Format(worked ? "Install.Notifications.DownloadFinished" : "Install.Notifications.DownloadFailed", name),
                result.Message);
        }

        private void NotifyUpdate(ModUpdateInstallResult result) =>
            Notify(
                result.Installed ? NotificationKind.UpdateInstalled : NotificationKind.UpdateFailed,
                Strings.Current.Format(
                    result.Installed ? "Install.Notifications.UpdateInstalled" : "Install.Notifications.UpdateFailed", result.ModuleName),
                result.Message);

        [RelayCommand]
        private void MarkNotificationsRead()
        {
            notifications.MarkAllRead();
            RefreshNotifications();
        }

        [RelayCommand]
        private void ClearNotifications()
        {
            notifications.Clear();
            RefreshNotifications();
        }
    }
}
