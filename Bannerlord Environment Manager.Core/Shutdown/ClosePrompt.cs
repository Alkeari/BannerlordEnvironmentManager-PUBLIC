using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Shutdown;

// What the window asks before it closes. Both reasons to ask can be true at once, so the choice is
// made in one place: the user gets one decision point, never a dialog followed by a second one.
//
// PrimaryWritesLoadOrder is what the primary button means, not what it says. Only a prompt raised
// over an unwritten load order has a write to attempt, and a primary that tried to flush one that
// was never held would report a failure about a file nothing had changed.
public sealed record ClosePrompt(
    string Title,
    string Content,
    string PrimaryButton,
    string SecondaryButton,
    string CloseButton,
    bool PrimaryWritesLoadOrder);

public static class ClosePromptDecision
{
    // Null means close without asking, which is the ordinary case: the load order is written as it is
    // edited, so closing normally loses nothing.
    //
    // A running download is asked about even though it survives the close. The child process dies with
    // BEM through its job object, and everything already transferred stays on the disk in the shape
    // Unfinished Downloads carries on from, so the prompt says that rather than warning about a loss
    // that does not happen. What is worth a question is the hours: a transfer that has to be restarted
    // from where it stopped is still a decision the user should make on purpose.
    public static ClosePrompt? For(bool loadOrderUnwritten, bool downloadRunning) =>
        (loadOrderUnwritten, downloadRunning) switch
        {
            (false, false) => null,

            (true, false) => new ClosePrompt(
                Strings.Current["App.CloseDialog.Title"],
                Strings.Current["App.CloseDialog.Content"],
                Strings.Current["App.CloseDialog.PrimaryButton"],
                Strings.Current["App.CloseDialog.SecondaryButton"],
                Strings.Current["App.CloseDialog.CloseButton"],
                PrimaryWritesLoadOrder: true),

            (false, true) => new ClosePrompt(
                Strings.Current["App.CloseDialog.Download.Title"],
                Strings.Current["App.CloseDialog.Download.Content"],
                Strings.Current["App.CloseDialog.Download.PrimaryButton"],
                string.Empty,
                Strings.Current["App.CloseDialog.CloseButton"],
                PrimaryWritesLoadOrder: false),

            (true, true) => new ClosePrompt(
                Strings.Current["App.CloseDialog.Both.Title"],
                Strings.Current["App.CloseDialog.Content"]
                    + System.Environment.NewLine + System.Environment.NewLine
                    + Strings.Current["App.CloseDialog.Download.Content"],
                Strings.Current["App.CloseDialog.Both.PrimaryButton"],
                Strings.Current["App.CloseDialog.Both.SecondaryButton"],
                Strings.Current["App.CloseDialog.CloseButton"],
                PrimaryWritesLoadOrder: true)
        };
}
