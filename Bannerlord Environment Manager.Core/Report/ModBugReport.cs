using System.Text;

namespace BannerlordEnvironmentManager.Core.Report;

public enum BugReportBlockKind
{
    Paragraph,

    // Pasted verbatim: a stack trace, a decompiled method, a block of XML. Shown rather than
    // summarized, because the thing found is the evidence.
    Fixed,

    Numbered,
    Bullets,

    // A plain rule. The report has almost no headings by design, so this is what separates it into
    // the two or three blocks a person reads.
    Rule
}

public sealed record BugReportBlock(BugReportBlockKind Kind, IReadOnlyList<string> Lines)
{
    public static BugReportBlock Paragraph(string text) => new(BugReportBlockKind.Paragraph, [text]);

    public static BugReportBlock Fixed(IReadOnlyList<string> lines) => new(BugReportBlockKind.Fixed, lines);

    public static BugReportBlock Numbered(IReadOnlyList<string> lines) => new(BugReportBlockKind.Numbered, lines);

    public static BugReportBlock Bullets(IReadOnlyList<string> lines) => new(BugReportBlockKind.Bullets, lines);

    public static BugReportBlock Rule() => new(BugReportBlockKind.Rule, []);
}

public enum BugReportStrength
{
    // A search concluded that this module is necessary for the crash, and there is a captured crash
    // to go with it.
    Strong,

    // Isolated by experiment, but something a reader would ask for is missing.
    Moderate,

    // Not isolated by experiment at all. Posting this is reporting a suspicion, and the report says so.
    Thin
}

// What the user is about to post, judged before they post it. Present and Missing are separate lists
// because "BEM found no crash report" and "BEM could not look for one" are different statements, and
// a reader of the banner has to be able to tell which happened.
public sealed record BugReportQuality(
    BugReportStrength Strength,
    string Headline,
    // The same sentence as Headline, always in English. The banner is BEM's own UI and follows the
    // app's language; the report body is pasted into a mod author's public thread, and that is
    // outside the program, so every word of it stays English whoever drafted it.
    string EnglishHeadline,
    IReadOnlyList<string> Present,
    IReadOnlyList<string> Missing,
    // The one launch that would move this report up a band, in the user's own terms. Never rendered
    // into the report body: it is what they do next, not something a mod author is told.
    string WouldStrengthen);

public sealed record ModBugReport(
    string Title,
    string SuggestedFileName,
    IReadOnlyList<BugReportBlock> Blocks,
    BugReportQuality Quality)
{
    public string Markdown => BugReportMarkup.Markdown(this);

    public string BBCode => BugReportMarkup.BBCode(this);
}

// The same report in the two places these get posted: a GitHub issue and a Nexus mod page comment.
// Same blocks, same order, same words. Only the wrapping differs, so there is never a version of the
// report that says something the other one does not.
public static class BugReportMarkup
{
    private const string Rule = "_________";

    public static string Markdown(ModBugReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var text = new StringBuilder();

        foreach (var block in report.Blocks)
        {
            switch (block.Kind)
            {
                case BugReportBlockKind.Rule:
                    text.AppendLine(Rule);
                    break;

                case BugReportBlockKind.Fixed:
                    // Four-space indentation rather than a fence, which is what the reference report
                    // uses and what renders the same everywhere Markdown is accepted.
                    foreach (var line in block.Lines)
                        text.AppendLine(line.Length == 0 ? string.Empty : "    " + line);

                    break;

                case BugReportBlockKind.Numbered:
                    for (var i = 0; i < block.Lines.Count; i++)
                        text.AppendLine($"{i + 1}. {block.Lines[i]}");

                    break;

                case BugReportBlockKind.Bullets:
                    foreach (var line in block.Lines)
                        text.AppendLine("- " + line);

                    break;

                default:
                    foreach (var line in block.Lines)
                        text.AppendLine(line);

                    break;
            }

            text.AppendLine();
        }

        return text.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string BBCode(ModBugReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var text = new StringBuilder();

        foreach (var block in report.Blocks)
        {
            switch (block.Kind)
            {
                case BugReportBlockKind.Rule:
                    text.AppendLine(Rule);
                    break;

                case BugReportBlockKind.Fixed:
                    text.AppendLine("[code]");

                    foreach (var line in block.Lines)
                        text.AppendLine(line);

                    text.AppendLine("[/code]");
                    break;

                case BugReportBlockKind.Numbered:
                    text.AppendLine("[list=1]");

                    foreach (var line in block.Lines)
                        text.AppendLine("[*]" + Plain(line));

                    text.AppendLine("[/list]");
                    break;

                case BugReportBlockKind.Bullets:
                    text.AppendLine("[list]");

                    foreach (var line in block.Lines)
                        text.AppendLine("[*]" + Plain(line));

                    text.AppendLine("[/list]");
                    break;

                default:
                    foreach (var line in block.Lines)
                        text.AppendLine(Plain(line));

                    break;
            }

            text.AppendLine();
        }

        return text.ToString().TrimEnd() + Environment.NewLine;
    }

    // BBCode has no inline code span. The backticks the Markdown form uses around a type or method
    // name would show up as literal characters on a mod page, so they come out. The words do not.
    private static string Plain(string line) => line.Replace("`", string.Empty, StringComparison.Ordinal);
}
