namespace BannerlordEnvironmentManager.Core.Launcher;

public enum LaunchWaitOutcome
{
    // The task watching the started process reported the run over.
    RunEnded,

    // No game process is on the machine any more, but the watching task is still pending. Something
    // between BEM and the process handle stopped answering, and waiting longer would never end.
    LostTrack
}

// Why this exists rather than a bare await on Process.WaitForExitAsync: a launch on a version that is
// not the resting one holds the canonical folders for the whole run, so the wait for the run to end is
// the one await in BEM with nothing bounding it. When it does not return, the Launch button stays
// disabled for the rest of the session and only restarting the app brings it back.
//
// The second opinion is the machine itself. The watching task and "is a game process running" fail
// independently: a handle that never signals is still a handle, while the process list is read fresh
// every poll. Once the game has been gone for the grace period, the run is over whatever the handle
// says, and the caller can go on and release the folders.
public static class LaunchCompletion
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    public static async Task<LaunchWaitOutcome> WaitForRunAsync(
        Task runEnded,
        Func<bool> gameIsRunning,
        TimeSpan? grace = null,
        TimeSpan? pollInterval = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(runEnded);
        ArgumentNullException.ThrowIfNull(gameIsRunning);

        var allowed = grace ?? DefaultGrace;
        var poll = pollInterval ?? DefaultPollInterval;
        var goneFor = TimeSpan.Zero;

        while (true)
        {
            if (runEnded.IsCompleted)
                return LaunchWaitOutcome.RunEnded;

            var finished = await Task.WhenAny(runEnded, Task.Delay(poll)).ConfigureAwait(false);

            if (ReferenceEquals(finished, runEnded))
                return LaunchWaitOutcome.RunEnded;

            // A probe that throws is not evidence the game is gone. Treating it as "still running"
            // keeps a machine that refuses to enumerate processes on the old behavior, waiting on the
            // handle, rather than tearing the folders down under a game that is still up.
            bool running;

            try
            {
                running = gameIsRunning();
            }
            catch (Exception ex)
            {
                log?.Invoke($"Could not tell whether the game is still running: {ex.Message}");
                running = true;
            }

            if (running)
            {
                goneFor = TimeSpan.Zero;
                continue;
            }

            goneFor += poll;

            if (goneFor < allowed)
                continue;

            log?.Invoke(
                $"No game process has been running for {allowed.TotalSeconds:0} seconds but the launch is "
                + "still waiting on the process handle; treating the run as over.");

            return LaunchWaitOutcome.LostTrack;
        }
    }
}
