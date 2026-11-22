namespace BannerlordEnvironmentManager.Core.Diagnostics;

/// <summary>
/// Notices when the UI thread stops answering. A background timer posts a heartbeat to the UI thread
/// every <see cref="Interval"/>; a heartbeat still unanswered after <see cref="Threshold"/> is a stall.
/// Code on the UI thread names what it is doing with <see cref="Begin"/>, so the log says which
/// operation was running, not only that something was.
/// </summary>
public sealed class UiStallMonitor : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(1);

    private readonly Func<long> nowMs;
    private readonly Action<Action> postToUi;
    private readonly Action<string> log;
    private readonly Func<string>? context;
    private readonly object gate = new();
    private readonly List<Operation> open = [];
    private readonly int uiThreadId;
    private Timer? timer;
    private long pendingSince = -1;
    private bool reported;
    private string reportedOperations = "";

    private sealed record Operation(string Name, long StartedMs, int ThreadId);

    public UiStallMonitor(Func<long> nowMs, Action<Action> postToUi, Action<string> log, int uiThreadId, Func<string>? context = null)
    {
        this.context = context;
        this.nowMs = nowMs;
        this.postToUi = postToUi;
        this.log = log;
        this.uiThreadId = uiThreadId;
    }

    /// <summary>The monitor the app runs, or null before it starts; operations tagged before then cost nothing.</summary>
    public static UiStallMonitor? Current { get; private set; }

    public static IDisposable Op(string name) => Current?.Begin(name) ?? NoOperation.Instance;

    public static UiStallMonitor Start(Action<Action> postToUi, Action<string> log, int uiThreadId)
    {
        // A stall with nothing tagged is often the runtime, not the code, so the log carries the collector's counts.
        var monitor = new UiStallMonitor(() => Environment.TickCount64, postToUi, log, uiThreadId,
            () => $"GC collections gen0 {GC.CollectionCount(0)}, gen1 {GC.CollectionCount(1)}, gen2 {GC.CollectionCount(2)}, pause so far {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms, heap {GC.GetTotalMemory(false) / 1048576} MB");
        monitor.timer = new Timer(_ => monitor.Tick(), null, Interval, Interval);
        Current = monitor;
        return monitor;
    }

    /// <summary>Marks the start of a named operation on the calling thread; dispose it when the operation ends.</summary>
    public IDisposable Begin(string name)
    {
        var operation = new Operation(name, nowMs(), Environment.CurrentManagedThreadId);

        lock (gate)
            open.Add(operation);

        return new Scope(this, operation);
    }

    /// <summary>One timer step: checks the pending heartbeat, then posts a new one when none is waiting.</summary>
    public void Tick()
    {
        string? message = null;
        var post = false;

        lock (gate)
        {
            var now = nowMs();

            if (pendingSince < 0)
            {
                pendingSince = now;
                post = true;
            }
            else if (!reported && now - pendingSince > Threshold.TotalMilliseconds)
            {
                reported = true;
                reportedOperations = Describe(now);
                message = $"UI thread has not answered for {now - pendingSince} ms. Running on it: {reportedOperations}."
                    + (context is null ? "" : " " + context() + ".");
            }
        }

        if (message is not null)
            log(message);

        if (post)
            postToUi(Beat);
    }

    /// <summary>Runs on the UI thread when the heartbeat is finally handled.</summary>
    public void Beat()
    {
        string? message = null;

        lock (gate)
        {
            var now = nowMs();

            if (reported)
            {
                message = $"UI thread answered again after {now - pendingSince} ms. It was running: {reportedOperations}."
                    + (context is null ? "" : " " + context() + ".");
                reported = false;
            }

            pendingSince = -1;
        }

        if (message is not null)
            log(message);
    }

    private string Describe(long now)
    {
        var names = open
            .Where(operation => operation.ThreadId == uiThreadId)
            .Select(operation => $"{operation.Name} (for {now - operation.StartedMs} ms)")
            .ToList();

        var described = names.Count == 0 ? "no tagged operation" : string.Join(" > ", names);

        // A collector pause or a saturated disk stalls the UI thread while the work that caused it runs
        // elsewhere, so the operations tagged on other threads are named too, as context and not as blame.
        var elsewhere = open
            .Where(operation => operation.ThreadId != uiThreadId)
            .Select(operation => $"{operation.Name} (for {now - operation.StartedMs} ms)")
            .ToList();

        return elsewhere.Count == 0 ? described : described + ". Also running off the UI thread: " + string.Join(", ", elsewhere);
    }

    private void End(Operation operation)
    {
        lock (gate)
            open.Remove(operation);
    }

    public void Dispose()
    {
        timer?.Dispose();

        if (ReferenceEquals(Current, this))
            Current = null;
    }

    private sealed class Scope(UiStallMonitor owner, Operation operation) : IDisposable
    {
        private int ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref ended, 1) == 0)
                owner.End(operation);
        }
    }

    private sealed class NoOperation : IDisposable
    {
        public static readonly NoOperation Instance = new();

        public void Dispose()
        {
        }
    }
}
