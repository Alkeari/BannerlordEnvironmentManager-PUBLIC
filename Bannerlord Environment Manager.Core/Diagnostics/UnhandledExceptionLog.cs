using System.Globalization;
using System.Text.RegularExpressions;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// What the engine's own error log says ended a run: its "Unhandled Exception Code 0x... at adress 0x..."
// block (the engine's spelling). The game writes it, then asks to generate a dump; when that is cancelled
// no crash folder and no report exist, and the process exit code is whatever the cancel left. This block
// is then the only evidence a run crashed at all.
public sealed record UnhandledException(uint Code, string Address, string LogPath)
{
    public string CodeText => $"0x{Code:X8}";
}

public static partial class UnhandledExceptionLog
{
    [GeneratedRegex(
        @"Unhandled Exception Code 0x(?<code>[0-9A-Fa-f]{1,8})(?:\s+at\s+ad{1,2}ress\s+(?<address>0x[0-9A-Fa-f]+))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex Block();

    public static UnhandledException? Parse(string text, string logPath = "")
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var match = Block().Match(text);

        if (!match.Success)
            return null;

        var code = uint.Parse(match.Groups["code"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        return new UnhandledException(code, match.Groups["address"].Value, logPath);
    }

    // The run's own error log, found by its process id, never by recency: a log from the run before is
    // not evidence about this one.
    public static UnhandledException? FindForRun(string logsFolder, int processId)
    {
        if (string.IsNullOrWhiteSpace(logsFolder) || processId <= 0 || !Directory.Exists(logsFolder))
            return null;

        try
        {
            foreach (var file in Directory.EnumerateFiles(logsFolder, "rgl_log_errors_*.txt", SearchOption.TopDirectoryOnly))
            {
                if (!FaultCorrelation.NameCarriesProcessId(Path.GetFileNameWithoutExtension(file), processId))
                    continue;

                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                return Parse(reader.ReadToEnd(), file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable log is not evidence either way.
        }

        return null;
    }

    // The newest error log in the folder that records an unhandled exception, whichever run wrote it, for a
    // crash that left nothing else behind: no Companion record, no dump. Newest by when it was written.
    public static (UnhandledException Block, DateTime WrittenUtc)? FindNewest(string logsFolder)
    {
        if (string.IsNullOrWhiteSpace(logsFolder) || !Directory.Exists(logsFolder))
            return null;

        try
        {
            foreach (var file in new DirectoryInfo(logsFolder).GetFiles("rgl_log_errors_*.txt")
                         .OrderByDescending(f => f.LastWriteTimeUtc))
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                if (Parse(reader.ReadToEnd(), file.FullName) is { } block)
                    return (block, file.LastWriteTimeUtc);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable log is not evidence either way.
        }

        return null;
    }
}
