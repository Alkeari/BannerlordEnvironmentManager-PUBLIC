namespace BannerlordEnvironmentManager.Core.Diagnostics;

// Work deliberately started and never waited on. A task dropped as a bare statement takes its
// exception with it: nothing ever observes the fault, so the failure is silent and the screen that
// started the work looks as though it simply did nothing. Two of tonight's undiagnosable failures
// were exactly that, so everything detached goes through here and a fault reaches the log instead.
public static class DetachedWork
{
    // For a caller that cannot await: the fault is logged rather than lost, and nothing else about
    // the caller changes. Never use this to avoid awaiting work whose result the caller needs.
    public static void Start(Task work, string what, Action<Exception, string> log) =>
        Observe(work, what, log);

    // The same, handing back the observation so a caller that needs to know the logging has
    // happened can wait for it. The returned task never faults and never cancels: a logger that
    // throws is swallowed rather than replacing the fault it was called to record with another
    // one nobody is watching either.
    public static Task Observe(Task work, string what, Action<Exception, string> log)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(log);

        return work.ContinueWith(
            finished =>
            {
                // Reading Exception is what marks the fault observed. A canceled task carries no
                // exception here and is not a failure, so it is passed over in silence.
                if (finished.Exception?.GetBaseException() is not { } error)
                    return;

                try
                {
                    log(error, what);
                }
                catch (Exception)
                {
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }
}
