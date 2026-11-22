namespace BannerlordEnvironmentManager.Core.Instances;

// Where the answer to a Steam Guard prompt comes from.
public enum SteamGuardSource
{
    // A code the user typed before DepotDownloader was started, which can be written to the process
    // the instant it asks.
    Prepared,

    // Nothing is held, so the user has to be asked while the process waits.
    Ask,

    // Already asked once in this run, and a Steam Guard code cannot be reused: asking again would
    // mean a parade of dialogs across a long download.
    Refuse
}

// Which answer a run gives to each Steam Guard prompt, kept out of the view model so the ordering
// can be tested without a dialog.
//
// The prepared code exists because a code typed while DepotDownloader is waiting is a code typed
// into a window that can close: the process exits, the write to its standard input throws "The pipe
// is being closed", and the user is told nothing useful about a download that just ended. A code
// collected before the process starts is available in microseconds, so that window is never open.
//
// A prepared code is offered exactly once, ever. Steam has either accepted it, in which case
// -remember-password stored a token and it will not be asked for again, or rejected it, in which
// case sending the same digits a second time cannot help.
public sealed class SteamGuardPlan
{
    private string? prepared;
    private bool askedThisRun;
    private bool refusedRepeat;

    public SteamGuardPlan(string? preparedCode) =>
        prepared = string.IsNullOrWhiteSpace(preparedCode) ? null : preparedCode.Trim();

    public bool HasPreparedCode => prepared is not null;

    public SteamGuardSource NextSource() =>
        prepared is not null
            ? SteamGuardSource.Prepared
            : askedThisRun ? SteamGuardSource.Refuse : SteamGuardSource.Ask;

    // Hands the prepared code over and forgets it in the same step, so nothing holds it after the
    // one prompt it was collected for.
    public string TakePrepared()
    {
        var code = prepared ?? string.Empty;
        prepared = null;
        return code;
    }

    public void Asked() => askedThisRun = true;

    // The sign-in that ends after this ended because BEM stopped it, not because the user declined
    // anything, and only the plan knows which of the two it was. It survives StartRun on purpose:
    // the refusal is what ends the whole flow, and the sentence reporting that comes later.
    public bool RefusedRepeatPrompt => refusedRepeat;

    public void RefuseRepeat() => refusedRepeat = true;

    // A retry is a new run of the tool, and each run gets its own single live ask.
    public void StartRun() => askedThisRun = false;

    // The code was never needed. Called when the flow ends, so an unused code does not outlive it.
    public void Discard() => prepared = null;
}
