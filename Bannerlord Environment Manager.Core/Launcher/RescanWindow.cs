namespace BannerlordEnvironmentManager.Core.Launcher;

// The whole span in which the load order on screen may be stale: from the first change event in the
// Modules folder (before the debouncer's quiet period ends and the rescan even starts) until the
// rescan that follows has finished. A Launch pressed anywhere in that span waits for it to close
// instead of launching a stale list or finding the button dead.
public sealed class RescanWindow
{
    private readonly object sync = new();
    private bool changePending;
    private bool rescanRunning;
    private TaskCompletionSource? idle;

    public bool Active
    {
        get { lock (sync) return changePending || rescanRunning; }
    }

    // A change event arrived; a rescan is coming but has not started.
    public void ChangeSeen()
    {
        lock (sync)
        {
            changePending = true;
            idle ??= NewSource();
        }
    }

    // The rescan started and now covers every change seen so far.
    public void RescanStarted()
    {
        lock (sync)
        {
            changePending = false;
            rescanRunning = true;
            idle ??= NewSource();
        }
    }

    // The rescan finished. A change seen while it ran keeps the window open for the rescan it causes.
    public void RescanFinished(bool anotherRescanFollows = false)
    {
        Close(() =>
        {
            rescanRunning = anotherRescanFollows;
            return !changePending && !rescanRunning;
        });
    }

    // The change was dropped without a rescan (a held change that must not be reloaded over).
    public void ChangeDropped() => Close(() =>
    {
        changePending = false;
        return !rescanRunning;
    });

    private void Close(Func<bool> update)
    {
        TaskCompletionSource? done = null;

        lock (sync)
        {
            if (update())
            {
                done = idle;
                idle = null;
            }
        }

        done?.TrySetResult();
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task WaitIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task pending;

            lock (sync)
            {
                if (idle is null)
                    return;

                pending = idle.Task;
            }

            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
