namespace BannerlordEnvironmentManager.Core.Launcher;

// Runs one piece of work at a time and tells a second caller it was turned away, so a button that
// stays pressable can say why a press did nothing instead of sitting dead.
public sealed class SingleFlight
{
    private int running;

    public bool Running => Volatile.Read(ref running) != 0;

    // False when work was already running and this call did not start any.
    public async Task<bool> TryRunAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return false;

        try
        {
            await work();
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }

        return true;
    }
}
