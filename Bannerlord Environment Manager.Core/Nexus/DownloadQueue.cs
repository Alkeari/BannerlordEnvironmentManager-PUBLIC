namespace BannerlordEnvironmentManager.Core.Nexus;

public enum DownloadQueueState
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Canceled
}

// One entry of the queue. Pausing a queued entry only holds it back; pausing a running one stops the
// transfer, and resuming starts it again from the beginning, because the Nexus downloader streams in
// one pass and keeps no partial file to continue from.
public sealed class DownloadQueueItem<TResult>
{
    internal readonly TaskCompletionSource<TResult?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal CancellationTokenSource? Transfer;

    internal bool StopWasPause;

    internal DownloadQueueItem(string name, CancellationToken owner, object? tag)
    {
        Name = name;
        Owner = owner;
        Tag = tag;
    }

    // Whatever the runner needs to know about this entry, carried by the one that queued it.
    public object? Tag { get; }

    public string Name { get; }

    public DownloadQueueState State { get; internal set; } = DownloadQueueState.Queued;

    public TResult? Result { get; internal set; }

    // Completes the first time the entry finishes, fails or is canceled, with null for a cancel. A retry
    // after a failure runs again but does not reopen it, so a caller that awaited the first run is
    // never held by a later one.
    public Task<TResult?> Finished => Completion.Task;

    internal CancellationToken Owner { get; }

    public bool CanPause => State is DownloadQueueState.Queued or DownloadQueueState.Running;

    public bool CanResume => State == DownloadQueueState.Paused;

    public bool CanRetry => State == DownloadQueueState.Failed;

    public bool CanCancel => State is DownloadQueueState.Queued or DownloadQueueState.Running or DownloadQueueState.Paused;

    public bool IsWaiting => State is DownloadQueueState.Queued or DownloadQueueState.Paused;
}

// A queue of downloads that runs a few at a time, in order, and can be paused, resumed, retried and
// reordered. It owns no transfer: the runner it is given does the work and says whether it worked.
public sealed class DownloadQueue<TResult>(
    Func<DownloadQueueItem<TResult>, CancellationToken, Task<TResult>> runner,
    Func<TResult, bool> succeeded,
    int maxConcurrent = 2)
{
    private readonly Lock gate = new();
    private readonly List<DownloadQueueItem<TResult>> items = [];

    // Raised after any entry changes state or the order changes. May fire on any thread.
    public event Action? Changed;

    public IReadOnlyList<DownloadQueueItem<TResult>> Items
    {
        get
        {
            lock (gate)
                return [.. items];
        }
    }

    public DownloadQueueItem<TResult> Enqueue(string name, CancellationToken owner = default, object? tag = null)
    {
        var item = new DownloadQueueItem<TResult>(name, owner, tag);

        lock (gate)
            items.Add(item);

        if (owner.CanBeCanceled)
            owner.Register(() => Cancel(item));

        Pump();
        return item;
    }

    public void Pause(DownloadQueueItem<TResult> item)
    {
        lock (gate)
        {
            if (!item.CanPause)
                return;

            if (item.State == DownloadQueueState.Queued)
            {
                item.State = DownloadQueueState.Paused;
            }
            else
            {
                item.StopWasPause = true;
                item.Transfer?.Cancel();
                return;
            }
        }

        Raise();
    }

    public void Resume(DownloadQueueItem<TResult> item)
    {
        lock (gate)
        {
            if (!item.CanResume)
                return;

            item.State = DownloadQueueState.Queued;
        }

        Pump();
    }

    public void Retry(DownloadQueueItem<TResult> item)
    {
        lock (gate)
        {
            if (!item.CanRetry)
                return;

            item.State = DownloadQueueState.Queued;
            item.Result = default;
        }

        Pump();
    }

    public void Cancel(DownloadQueueItem<TResult> item)
    {
        bool completeNow;

        lock (gate)
        {
            if (!item.CanCancel)
                return;

            item.StopWasPause = false;
            completeNow = item.State != DownloadQueueState.Running;

            if (completeNow)
                item.State = DownloadQueueState.Canceled;
            else
                item.Transfer?.Cancel();
        }

        if (completeNow)
            item.Completion.TrySetResult(default);

        Raise();
    }

    // Moves a waiting entry one place earlier or later among the entries that have not started.
    public bool Move(DownloadQueueItem<TResult> item, int direction)
    {
        lock (gate)
        {
            if (!item.IsWaiting)
                return false;

            var at = items.IndexOf(item);
            var step = Math.Sign(direction);

            for (var next = at + step; next >= 0 && next < items.Count; next += step)
            {
                if (!items[next].IsWaiting)
                    continue;

                items.RemoveAt(at);
                items.Insert(next, item);
                Raise();
                return true;
            }
        }

        return false;
    }

    public void Remove(DownloadQueueItem<TResult> item)
    {
        lock (gate)
        {
            if (item.State is DownloadQueueState.Queued or DownloadQueueState.Running)
                return;

            items.Remove(item);
        }

        Raise();
    }

    private void Pump()
    {
        var starting = new List<DownloadQueueItem<TResult>>();

        lock (gate)
        {
            var running = items.Count(item => item.State == DownloadQueueState.Running);

            foreach (var item in items.Where(item => item.State == DownloadQueueState.Queued))
            {
                if (running >= maxConcurrent)
                    break;

                item.State = DownloadQueueState.Running;
                item.StopWasPause = false;
                item.Transfer = new CancellationTokenSource();
                starting.Add(item);
                running++;
            }
        }

        foreach (var item in starting)
            _ = Task.Run(() => RunAsync(item));

        Raise();
    }

    private async Task RunAsync(DownloadQueueItem<TResult> item)
    {
        TResult? result = default;
        var failed = false;
        var stopped = false;

        try
        {
            result = await runner(item, item.Transfer!.Token);
            failed = !succeeded(result);
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failed = true;
        }

        var complete = false;
        var canceled = false;

        lock (gate)
        {
            item.Transfer?.Dispose();
            item.Transfer = null;

            if (stopped && item.StopWasPause)
            {
                item.State = DownloadQueueState.Paused;
            }
            else if (stopped)
            {
                item.State = DownloadQueueState.Canceled;
                complete = true;
                canceled = true;
            }
            else
            {
                item.Result = result;
                item.State = failed ? DownloadQueueState.Failed : DownloadQueueState.Completed;
                complete = true;
            }

            item.StopWasPause = false;
        }

        if (complete)
            item.Completion.TrySetResult(canceled ? default : result);

        Pump();
    }

    private void Raise() => Changed?.Invoke();
}
