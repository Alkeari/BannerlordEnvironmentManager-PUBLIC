namespace BannerlordEnvironmentManager.Core.Game;

/// <summary>
/// Decides whether a re-read of the game folder is due when the window regains focus: not when one began
/// less than <see cref="Interval"/> ago, and not while one is still running. The clock is injected so the
/// rule is tested without waiting.
/// </summary>
public sealed class RefreshThrottle(TimeSpan interval, Func<DateTimeOffset>? clock = null)
{
    public static TimeSpan DefaultInterval { get; } = TimeSpan.FromSeconds(5);

    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object gate = new();
    private DateTimeOffset? last;
    private bool running;

    public TimeSpan Interval { get; } = interval;

    /// <summary>True when the caller should refresh now; it must then call <see cref="Finish"/>.</summary>
    public bool TryBegin()
    {
        lock (gate)
        {
            if (running)
                return false;

            var current = now();

            if (last is { } previous && current - previous < Interval)
                return false;

            running = true;
            last = current;
            return true;
        }
    }

    /// <summary>Ends a refresh begun by <see cref="TryBegin"/>; the interval counts from its end.</summary>
    public void Finish()
    {
        lock (gate)
        {
            running = false;
            last = now();
        }
    }

    /// <summary>Counts a refresh that happened for another reason (BEM's own rescan) as the latest.</summary>
    public void Note()
    {
        lock (gate)
            last = now();
    }
}
