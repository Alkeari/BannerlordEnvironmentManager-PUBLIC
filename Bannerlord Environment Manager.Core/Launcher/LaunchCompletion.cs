namespace BannerlordEnvironmentManager.Core.Launcher;

public enum LaunchWaitOutcome
{
    // The task watching the started process reported an exit, and no process of the run was alive on
    // two reads in a row after it.
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

    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromMinutes(1);

    public static async Task<LaunchWaitOutcome> WaitForRunAsync(
        Task runEnded,
        Func<bool> gameIsRunning,
        TimeSpan? grace = null,
        TimeSpan? pollInterval = null,
        Action<string>? log = null,
        TimeSpan? heartbeat = null)
    {
        ArgumentNullException.ThrowIfNull(runEnded);
        ArgumentNullException.ThrowIfNull(gameIsRunning);

        var allowed = grace ?? DefaultGrace;
        var poll = pollInterval ?? DefaultPollInterval;
        var goneFor = TimeSpan.Zero;
        var every = heartbeat ?? HeartbeatEvery;
        var waited = TimeSpan.Zero;
        var lastNote = TimeSpan.Zero;

        var handleEnded = false;
        var goneReads = 0;
        var notedOutlived = false;

        while (true)
        {
            if (!handleEnded && runEnded.IsCompleted)
                handleEnded = true;

            if (!handleEnded)
            {
                var finished = await Task.WhenAny(runEnded, Task.Delay(poll)).ConfigureAwait(false);

                if (ReferenceEquals(finished, runEnded))
                    continue;
            }

            // A probe that throws is not evidence the game is gone. Treating it as "still running"
            // keeps a machine that refuses to enumerate processes waiting, rather than tearing the
            // folders down under a game that is still up.
            var running = RunLiveness.Probe(gameIsRunning, log);

            if (handleEnded)
            {
                // The handle ending is not the run ending: BLSE Standalone ended its own process with
                // exit code -1 five seconds in while the game it started went on loading, and the
                // teardown that followed killed it. Two reads in a row with nothing alive, so a game
                // that restarts itself is not caught in the gap.
                if (running)
                {
                    if (!notedOutlived)
                    {
                        notedOutlived = true;
                        log?.Invoke("The launched process has exited, but a process of the run is still alive; the junctions stay up until none is.");
                    }

                    goneReads = 0;
                    waited += poll;
                    lastNote = Heartbeat(log, waited, lastNote, every, "the launched process has exited but a process of the run is still alive");
                }
                else if (++goneReads >= 2)
                {
                    return LaunchWaitOutcome.RunEnded;
                }

                await Task.Delay(poll).ConfigureAwait(false);
                continue;
            }

            if (running)
            {
                goneFor = TimeSpan.Zero;
                waited += poll;
                lastNote = Heartbeat(log, waited, lastNote, every, "a game process is on the machine and the process handle has not reported an exit");
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

    // A wait with no sign of life was how a run that never tore down went unexplained: the log was
    // silent from the activation to the kill. One line a minute says what it waits on.
    private static TimeSpan Heartbeat(Action<string>? log, TimeSpan waited, TimeSpan lastNote, TimeSpan every, string what)
    {
        if (waited - lastNote < every)
            return lastNote;

        log?.Invoke($"Still waiting on the run after {waited.TotalMinutes:0} minute(s): {what}.");
        return waited;
    }
}
