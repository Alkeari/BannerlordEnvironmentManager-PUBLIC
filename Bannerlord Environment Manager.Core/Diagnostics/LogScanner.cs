using System.Text.RegularExpressions;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public enum LogSignal
{
    StackFrame,
    Error,
    Exception,
    Fatal
}

public sealed record LogFinding(
    int LineNumber,
    LogSignal Signal,
    string Headline,
    IReadOnlyList<string> Excerpt,
    int MatchCount);

public sealed record LogScan(
    string Path,
    IReadOnlyList<LogFinding> Findings,
    int MatchingLines,
    string? Error);

// Pulls the exception-shaped passages out of a log. It says what the log says and nothing more: it
// does not decide which module is at fault. The crash attribution rules in Attribution are the only
// thing in BEM that names a suspect, and they run on parsed crash reports, not on these lines.
public static class LogScanner
{
    public const int DefaultMaxFindings = 25;
    public const int DefaultContextLines = 3;
    public const int MaxLineLength = 500;

    private static readonly Regex Fatal = new(@"\bFATAL\b|\[FTL\]|\[Fatal\]", RegexOptions.Compiled);
    private static readonly Regex Exception = new(@"\b\w*Exception\b", RegexOptions.Compiled);
    private static readonly Regex Error = new(@"\bERROR\b|\[ERR\]|\[Error\]", RegexOptions.Compiled);
    private static readonly Regex StackFrame = new(@"^\s+at\s+\S", RegexOptions.Compiled);

    public static LogScan Scan(
        string path,
        int maxFindings = DefaultMaxFindings,
        int contextLines = DefaultContextLines)
    {
        var findings = new List<LogFinding>();
        var before = new Queue<string>();
        var matchingLines = 0;

        Passage? open = null;

        try
        {
            using var reader = new StreamReader(path);

            var number = 0;

            while (reader.ReadLine() is { } raw)
            {
                number++;

                var line = Shorten(raw);
                var signal = Detect(line);

                if (signal is { } found)
                {
                    matchingLines++;

                    if (open is not null)
                    {
                        open.Add(line, found);
                    }
                    else if (findings.Count < maxFindings)
                    {
                        open = new Passage(number, line, found, [.. before], contextLines);
                    }

                    continue;
                }

                if (open is not null)
                {
                    if (open.WantsMore)
                        open.Trail(line);
                    else
                    {
                        findings.Add(open.Close());
                        open = null;
                    }
                }

                before.Enqueue(line);

                while (before.Count > contextLines)
                    before.Dequeue();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new LogScan(path, findings, matchingLines, $"{Path.GetFileName(path)} could not be read: {ex.Message}");
        }

        if (open is not null)
            findings.Add(open.Close());

        return new LogScan(path, findings, matchingLines, null);
    }

    private static string Shorten(string line) =>
        line.Length <= MaxLineLength ? line : line[..MaxLineLength] + " ...";

    private static LogSignal? Detect(string line)
    {
        if (Fatal.IsMatch(line))
            return LogSignal.Fatal;

        if (Exception.IsMatch(line))
            return LogSignal.Exception;

        if (Error.IsMatch(line))
            return LogSignal.Error;

        return StackFrame.IsMatch(line) ? LogSignal.StackFrame : null;
    }

    private sealed class Passage(int lineNumber, string headline, LogSignal signal, List<string> before, int contextLines)
    {
        private readonly List<string> lines = [.. before, headline];
        private readonly int context = contextLines;
        private LogSignal strongest = signal;
        private int matches = 1;
        private int trailing = contextLines;

        public bool WantsMore => trailing > 0;

        public void Add(string line, LogSignal found)
        {
            lines.Add(line);
            matches++;
            trailing = context;

            if (found > strongest)
                strongest = found;
        }

        public void Trail(string line)
        {
            lines.Add(line);
            trailing--;
        }

        public LogFinding Close() => new(lineNumber, strongest, headline.Trim(), lines, matches);
    }
}
