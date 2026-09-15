namespace BannerlordEnvironmentManager.Core.Modules;

// A Modules folder extraction produces a burst of file system events, one per file. Notify() is meant
// to be called once per raw event; onSettled fires once, after the quiet period has passed with no
// further Notify() call, collapsing the burst into the single rescan it should cause. Holds no
// reference to any FileSystemWatcher or UI thread, so it is testable by calling Notify() directly with
// no file system and no watcher involved.
public sealed class ModulesFolderChangeDebouncer : IDisposable
{
    private readonly TimeSpan quietPeriod;
    private readonly Action onSettled;
    private readonly Timer timer;
    private readonly Lock gate = new();
    private bool disposed;

    public ModulesFolderChangeDebouncer(TimeSpan quietPeriod, Action onSettled)
    {
        this.quietPeriod = quietPeriod;
        this.onSettled = onSettled;
        timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Notify()
    {
        lock (gate)
        {
            if (disposed)
                return;

            timer.Change(quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    // The callback runs inside the lock, not just the disposed check ahead of it: a Dispose() that
    // starts while this is running has to wait for onSettled() to finish before it can mark this
    // disposed, so it can never observe "not disposed" and then have the callback fire regardless.
    // Safe only because onSettled() never calls back into this debouncer itself (it enqueues work on
    // another thread and returns); a callback that reentered Notify() or Dispose() here would deadlock.
    private void Fire()
    {
        lock (gate)
        {
            if (disposed)
                return;

            onSettled();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;

            disposed = true;
            timer.Dispose();
        }
    }
}
