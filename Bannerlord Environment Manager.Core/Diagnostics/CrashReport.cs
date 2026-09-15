using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public enum CrashNodeKind
{
    Root,
    Wrapper
}

public sealed record ExceptionNode(
    string TypeFullName,
    string Message,
    IReadOnlyList<CrashFrame> Frames,
    CrashNodeKind Kind,
    int WrapperIndex)
{
    public string Label => Kind is CrashNodeKind.Root ? "ROOT" : $"WRAPPER[{WrapperIndex}]";
}

public sealed partial record CrashReport(
    string RawText,
    IReadOnlyList<ExceptionNode> Nodes,
    IReadOnlyList<CrashReport> AggregatedInners,
    string? Error)
{
    private const string InnerBoundary = "End of inner exception stack trace";
    private const string AggregateMarker = "---> (Inner Exception #";
    private const string ChainSeparator = " ---> ";
    private const int HeaderSearchLines = 10;

    [GeneratedRegex(@"^\s*[\w.+`\[\]]*(Exception|Error)\s*:")]
    private static partial Regex ExceptionHeader { get; }

    [GeneratedRegex(@"Exception|Error")]
    private static partial Regex ExceptionWord { get; }

    public bool Failed => Error is not null;

    // Parsing succeeds on a frame line alone, so a log that logged a stack trace parses without
    // error while naming no exception. Discovery has to reject those or a log, which is rewritten
    // every session, outranks every real report by write time.
    public bool NamesException =>
        Nodes.Any(node => !string.IsNullOrWhiteSpace(node.TypeFullName))
        || AggregatedInners.Any(inner => inner.NamesException);

    // The innermost exception, and the only node ranking may be based on. A TargetInvocationException
    // or any other reflection wrapper carries no evidence about who faulted.
    public ExceptionNode? Root => Nodes.Count > 0 ? Nodes[0] : null;

    public ExceptionNode? Outermost => Nodes.Count > 0 ? Nodes[^1] : null;

    public IReadOnlyList<ExceptionNode> Wrappers => [.. Nodes.Skip(1)];

    public CrashFrame? FaultFrame => Root?.Frames.FirstOrDefault();

    public static CrashReport Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new CrashReport(
                text ?? string.Empty, [], [], Strings.Current["Core.Diagnostics.CrashReport.NoTextSupplied"]);
        }

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var (chain, sections) = SplitAggregatedInners(lines);
        var (nodes, error) = ParseChain(chain);

        return new CrashReport(text, nodes, [.. sections.Select(Parse)], error);
    }

    private static (List<string> Chain, List<string> Sections) SplitAggregatedInners(string[] lines)
    {
        var chain = new List<string>();
        var sections = new List<List<string>>();

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith(AggregateMarker, StringComparison.Ordinal))
            {
                sections.Add([StripMarker(trimmed)]);
                continue;
            }

            (sections.Count > 0 ? sections[^1] : chain).Add(line.Replace("<---", string.Empty));
        }

        return (chain, [.. sections.Select(section => string.Join('\n', section))]);
    }

    private static string StripMarker(string line)
    {
        var close = line.IndexOf(')');
        var body = close >= 0 && close + 1 < line.Length ? line[(close + 1)..] : line;

        return body.Replace("<---", string.Empty).TrimStart();
    }

    private static (List<ExceptionNode> Nodes, string? Error) ParseChain(List<string> lines)
    {
        var firstFrame = lines.FindIndex(StackFrameParser.IsFrameLine);
        var headerIndex = FindHeader(lines, firstFrame);

        if (headerIndex < 0 && firstFrame < 0)
            return ([], Strings.Current["Core.Diagnostics.CrashReport.NoHeaderOrFrame"]);

        var segments = headerIndex < 0
            ? []
            : ParseHeader(lines[headerIndex]);

        var blocks = ReadFrameBlocks(lines, firstFrame);
        var nodes = new List<ExceptionNode>();

        for (var i = 0; i < Math.Max(segments.Count, blocks.Count); i++)
        {
            // Segments run outermost first, frame blocks innermost first.
            var segment = segments.Count - 1 - i;

            nodes.Add(new ExceptionNode(
                segment >= 0 ? segments[segment].TypeFullName : string.Empty,
                segment >= 0 ? segments[segment].Message : string.Empty,
                i < blocks.Count ? blocks[i] : [],
                i == 0 ? CrashNodeKind.Root : CrashNodeKind.Wrapper,
                i == 0 ? -1 : i - 1));
        }

        return (nodes, null);
    }

    private static int FindHeader(List<string> lines, int firstFrame)
    {
        if (firstFrame < 0)
            return lines.FindIndex(ExceptionHeader.IsMatch);

        // A header must name an exception. Taking the nearest non-blank line unconditionally turned
        // an "[INF]" line into the reported exception type whenever a log had logged a stack trace.
        // The search is bounded because a header belongs to the trace directly beneath it: the
        // captured-report format spends three lines on "Exception information", "Type:" and
        // "Message:" before "Stack trace:", and anything further up belongs to something else.
        var limit = Math.Max(0, firstFrame - HeaderSearchLines);

        for (var i = firstFrame - 1; i >= limit; i--)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]) && ExceptionWord.IsMatch(lines[i]))
                return i;
        }

        return -1;
    }

    private static List<(string TypeFullName, string Message)> ParseHeader(string header)
    {
        var segments = new List<(string, string)>();

        foreach (var part in header.Split(ChainSeparator, StringSplitOptions.None))
        {
            var text = part.Trim();
            var colon = text.IndexOf(": ", StringComparison.Ordinal);

            segments.Add(colon < 0
                ? (text.TrimEnd(':'), string.Empty)
                : (text[..colon], text[(colon + 2)..]));
        }

        return segments;
    }

    private static List<List<CrashFrame>> ReadFrameBlocks(List<string> lines, int firstFrame)
    {
        List<List<CrashFrame>> blocks = [[]];

        if (firstFrame < 0)
            return blocks;

        for (var i = firstFrame; i < lines.Count; i++)
        {
            var frame = StackFrameParser.Parse(lines[i], blocks[^1].Count);

            if (frame is not null)
            {
                blocks[^1].Add(frame);
                continue;
            }

            var trimmed = lines[i].Trim();

            if (trimmed.Contains(InnerBoundary, StringComparison.OrdinalIgnoreCase))
            {
                blocks.Add([]);
                continue;
            }

            // Other dashed markers, such as the async resume marker, sit inside a single trace and
            // must not split it. Anything else means the dump ended and the report resumed.
            if (trimmed.Length > 0 && !trimmed.StartsWith("---", StringComparison.Ordinal))
                break;
        }

        if (blocks.Count > 1 && blocks[^1].Count == 0)
            blocks.RemoveAt(blocks.Count - 1);

        return blocks;
    }
}
