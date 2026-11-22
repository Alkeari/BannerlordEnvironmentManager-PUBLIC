namespace BannerlordEnvironmentManager.Core.Launcher;

// Lets a press that arrives while a rescan is running wait for it instead of finding the button dead:
// the rescan opens the gate, and a launch awaits it going idle before reading the result.
public sealed class InFlightGate
{
    private readonly object sync = new();
    private TaskCompletionSource? idle;

    public bool Active
    {
        get { lock (sync) return idle is not null; }
    }

    public void Enter()
    {
        lock (sync)
            idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Exit()
    {
        TaskCompletionSource? done;

        lock (sync)
        {
            done = idle;
            idle = null;
        }

        done?.TrySetResult();
    }

    // Returns once nothing is in flight, including work that started while waiting.
    public async Task WaitIdleAsync()
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

            await pending.ConfigureAwait(false);
        }
    }
}
