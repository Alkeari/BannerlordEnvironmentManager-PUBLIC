using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Launcher;

// A launch has two moments, and on a version that is not the resting one they are a play session
// apart: the game starting, and the run being over. BEM holds the canonical folders for the whole run,
// so the await that waits for the game to exit spans hours, and anything sequenced after it happens
// hours late. That is how "Minimize on launch" came to minimize the window when the player came back
// and how the success line came to be written once the game had already stopped.
//
// So the two moments are named here rather than left implicit in the order of statements around an
// await. Whoever starts the game calls GameStarted the instant it is up; whoever waits calls
// RunFinished when the wait is over. The rules that keep them honest are the whole point of the type:
// nothing is announced twice, an outcome is never reported for a launch that never started, and a run
// BEM cannot see the end of never gets an end reported at all.
public sealed class LaunchSequence
{
    private readonly Action<string> report;
    private readonly Action? onGameStarted;

    public LaunchSequence(Action<string> report, Action? onGameStarted = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        this.report = report;
        this.onGameStarted = onGameStarted;
    }

    public bool HasStarted { get; private set; }

    public bool HasFinished { get; private set; }

    // False when the game was handed off to something BEM holds no handle on, which is every launcher
    // target and the Steam URI. There is no end of run to wait for or to report.
    public bool IsWatchingRun { get; private set; }

    // Called the moment the launch target is up, before anything waits on the run. Everything the press
    // of Launch was an answer to belongs here: the window minimize, and the line that says it started.
    public void GameStarted(string message, bool watchingTheRun)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (HasStarted)
            return;

        HasStarted = true;
        IsWatchingRun = watchingTheRun;

        onGameStarted?.Invoke();
        report(message);
    }

    // The run BEM was watching is over. Silent for a launch that never started, so a failed start can
    // never produce a line describing how its run went, and silent for a hand-off, where the status
    // still rightly reads as the game having been started.
    public void RunFinished(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!HasStarted || !IsWatchingRun || HasFinished)
            return;

        HasFinished = true;
        report(message);
    }

    // What the status line says at the start. Every piece of it describes the launch that is beginning:
    // which target, what happens to a crash, what the preflight found, and what the plan changed.
    public static string StartedMessage(
        string targetName,
        string crashHandling,
        bool capturingArtifacts,
        string preflightNote,
        string planNote)
    {
        ArgumentNullException.ThrowIfNull(targetName);
        ArgumentNullException.ThrowIfNull(crashHandling);
        ArgumentNullException.ThrowIfNull(preflightNote);
        ArgumentNullException.ThrowIfNull(planNote);

        return Strings.Current.Format("Environment.Launch.Success", targetName, crashHandling)
            + (capturingArtifacts
                ? Strings.Current["Environment.Launch.CapturingNote"]
                : Strings.Current["Environment.Launch.NotCapturingNote"])
            + (preflightNote.Length == 0 ? string.Empty : $" {preflightNote}")
            + (planNote.Length == 0 ? string.Empty : $" {planNote}");
    }

    // What the status line says when the run is over. Losing sight of the process already says the
    // folders were released, so the folders sentence is not repeated after it.
    public static string FinishedMessage(bool junctionsUsed, LaunchWaitOutcome outcome) =>
        outcome == LaunchWaitOutcome.LostTrack
            ? Strings.Current["Environment.Launch.RunFinished"]
                + $" {Strings.Current["Environment.Launch.LostTrack"]}"
            : Strings.Current["Environment.Launch.RunFinished"]
                + (junctionsUsed ? Strings.Current["Environment.Launch.FoldersRestored"] : string.Empty);
}
