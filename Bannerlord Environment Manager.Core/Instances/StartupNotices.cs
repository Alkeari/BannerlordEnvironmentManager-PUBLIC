namespace BannerlordEnvironmentManager.Core.Instances;

// What the startup passes found, held until the shell exists to say it.
//
// Two surfaces, because they answer different questions. The summary is the whole story and it goes
// to the Versions status line, where a user who wants to know what BEM did at startup looks. What
// must be seen is the subset that changed something on the user's own disk, and it goes to a dialog
// on the start that changed it, because the status line at the bottom of one page is not an answer
// for a user standing on another page: five instance folders were once renamed and the
// only trace available was the log.
//
// The two are taken independently. Neither surface may swallow the other's copy, and each hands its
// text over exactly once, so a later manual refresh does not repeat a notice from minutes ago.
public sealed class StartupNotices
{
    private readonly List<string> everything = [];
    private readonly List<string> mustBeSeen = [];

    private bool summaryTaken;
    private bool acknowledgementTaken;

    public static StartupNotices Pending { get; } = new();

    // Joined rather than replaced: two startup steps can both have something to say, and the second
    // must not silently drop the first.
    public void Post(string message, bool mustBeSeen = false)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        everything.Add(message);

        if (mustBeSeen)
            this.mustBeSeen.Add(message);
    }

    // A folder that moved on disk is never a quiet line: the report decides for itself that it has to
    // be acknowledged, so no caller can post one of these without it reaching the dialog.
    public void Post(InstanceFolderAlignmentReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (!report.AnythingToSay)
            return;

        Post(report.Describe(), report.AnythingMoved);
    }

    public string TakeSummary() => Take(everything, ref summaryTaken);

    public string TakeWhatMustBeSeen() => Take(mustBeSeen, ref acknowledgementTaken);

    private static string Take(List<string> lines, ref bool alreadyTaken)
    {
        if (alreadyTaken)
            return string.Empty;

        alreadyTaken = true;

        return string.Join(" ", lines);
    }
}
