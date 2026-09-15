namespace BannerlordEnvironmentManager.Core.Install;

// Typing in a search box raises one change per keystroke, and rebuilding a visible list on every one
// of them makes the box fight the typist. RequestAsync waits out a quiet period first, and a request
// arriving while one is waiting supersedes it rather than stacking a second timer beside it, so a
// burst of keystrokes rebuilds the list once, after the typing stops. The quiet period is a
// constructor argument so a test can state the same facts in milliseconds instead of waiting the real
// interval out.
public sealed class SupersedingDebounce : IDisposable
{
    private readonly TimeSpan quietPeriod;
    private readonly Lock gate = new();
    private CancellationTokenSource? pending;
    private bool disposed;

    public SupersedingDebounce(TimeSpan quietPeriod) => this.quietPeriod = quietPeriod;

    // True when the quiet period passed with no later request, meaning this caller should do the
    // work. False when a later request superseded this one or the debounce was disposed. It never
    // throws and never cancels: a superseded caller is not a failure, and a caller that has to catch
    // its own supersession is a caller that will one day forget to.
    public Task<bool> RequestAsync()
    {
        CancellationTokenSource source;
        CancellationToken token;

        lock (gate)
        {
            if (disposed)
                return Task.FromResult(false);

            CancelPending();
            source = new CancellationTokenSource();
            // Read inside the lock: once superseded, the source is disposed and cannot be asked
            // for its token any more, but the token itself stays usable and reads as canceled.
            token = source.Token;
            pending = source;
        }

        return WaitAsync(source, token);
    }

    private async Task<bool> WaitAsync(CancellationTokenSource source, CancellationToken token)
    {
        try
        {
            await Task.Delay(quietPeriod, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        lock (gate)
        {
            if (disposed || !ReferenceEquals(pending, source))
                return false;

            CancelPending();
        }

        return true;
    }

    // The caller holds the gate. Cancel before dispose, always, so a token handed out earlier reads
    // as canceled rather than as a source that was disposed while somebody was still waiting on it.
    private void CancelPending()
    {
        if (pending is null)
            return;

        pending.Cancel();
        pending.Dispose();
        pending = null;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;

            disposed = true;
            CancelPending();
        }
    }
}
