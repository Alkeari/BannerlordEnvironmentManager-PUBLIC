using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Nexus;

namespace BannerlordEnvironmentManager.ViewModels
{
    // One row of the Library's Downloads queue: a Nexus download waiting, running, paused or finished.
    // The transfer is the existing NxmLinks downloader and the order is the DownloadQueue's; this row
    // shows both and owns the buttons. The downloader streams in one pass and keeps no partial file, so
    // pausing a running download stops it and resuming starts it again from the beginning. The Pause
    // tooltip says so, since a button that implied otherwise would mislead.
    public partial class DownloadItemViewModel : ObservableObject
    {
        private readonly Action<DownloadItemViewModel> dismiss;
        private readonly Action<DownloadItemViewModel, int> move;
        private DownloadQueue<NxmDownloadResult>? queue;
        private DownloadQueueItem<NxmDownloadResult>? item;

        public DownloadItemViewModel(string name, Action<DownloadItemViewModel> dismiss, Action<DownloadItemViewModel, int> move)
        {
            ArgumentNullException.ThrowIfNull(dismiss);
            ArgumentNullException.ThrowIfNull(move);

            Name = name;
            this.dismiss = dismiss;
            this.move = move;
            IsRunning = true;
            IsIndeterminate = true;
            Status = Strings.Current["Install.Downloads.State.Queued"];
            Message = string.Empty;
        }

        public string Name { get; }

        public DownloadQueueItem<NxmDownloadResult>? QueueItem => item;

        // Not finished: still waiting, running or paused. Cancel shows while this holds.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsFinished))]
        public partial bool IsRunning { get; set; }

        public bool IsFinished => !IsRunning;

        [ObservableProperty]
        public partial bool IsTransferring { get; set; }

        [ObservableProperty]
        public partial bool CanPause { get; set; }

        [ObservableProperty]
        public partial bool CanResume { get; set; }

        [ObservableProperty]
        public partial bool CanRetry { get; set; }

        [ObservableProperty]
        public partial bool CanMove { get; set; }

        [ObservableProperty]
        public partial bool IsIndeterminate { get; set; }

        [ObservableProperty]
        public partial double Percent { get; set; }

        [ObservableProperty]
        public partial string Status { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Message { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool Succeeded { get; set; }

        public void Attach(DownloadQueue<NxmDownloadResult> owner, DownloadQueueItem<NxmDownloadResult> queued)
        {
            queue = owner;
            item = queued;
            Refresh();
        }

        // Reads the queue's state into the row. A finished row keeps the words Finish gave it.
        public void Refresh()
        {
            if (item is not { } current)
                return;

            var state = current.State;

            IsRunning = state is DownloadQueueState.Queued or DownloadQueueState.Running or DownloadQueueState.Paused;
            IsTransferring = state == DownloadQueueState.Running;
            CanPause = current.CanPause;
            CanResume = current.CanResume;
            CanRetry = current.CanRetry;
            CanMove = current.IsWaiting;

            switch (state)
            {
                case DownloadQueueState.Queued:
                    Status = Strings.Current["Install.Downloads.State.Queued"];
                    Message = string.Empty;
                    IsIndeterminate = true;
                    Percent = 0;
                    break;
                case DownloadQueueState.Paused:
                    Status = Strings.Current["Install.Downloads.State.Paused"];
                    Message = Strings.Current["Install.Downloads.State.Paused.Message"];
                    Percent = 0;
                    break;
                case DownloadQueueState.Running:
                    Status = Strings.Current["Install.Nexus.DownloadingTitle"];
                    break;
                case DownloadQueueState.Canceled:
                    Succeeded = false;
                    Status = Strings.Current["Install.Nexus.Title.Canceled"];
                    Message = Strings.Current["Install.Nexus.DownloadCanceled"];
                    break;
            }
        }

        public void Report(NxmDownloadProgress report)
        {
            IsIndeterminate = report.Fraction is null;
            Percent = (report.Fraction ?? 0) * 100;
            Message = report.Describe();
        }

        public void Finish(bool succeeded, string status, string message)
        {
            IsIndeterminate = false;
            Succeeded = succeeded;
            Percent = succeeded ? 100 : 0;
            Status = status;
            Message = message;
        }

        [RelayCommand]
        private void Cancel()
        {
            if (queue is not null && item is not null)
                queue.Cancel(item);
        }

        [RelayCommand]
        private void Pause()
        {
            if (queue is not null && item is not null)
                queue.Pause(item);
        }

        [RelayCommand]
        private void Resume()
        {
            if (queue is not null && item is not null)
                queue.Resume(item);
        }

        [RelayCommand]
        private void Retry()
        {
            if (queue is not null && item is not null)
                queue.Retry(item);
        }

        [RelayCommand]
        private void MoveUp() => move(this, -1);

        [RelayCommand]
        private void MoveDown() => move(this, 1);

        [RelayCommand]
        private void Dismiss() => dismiss(this);
    }
}
