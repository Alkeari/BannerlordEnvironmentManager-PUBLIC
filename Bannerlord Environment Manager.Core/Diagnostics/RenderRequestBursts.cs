using System.Globalization;
using System.Text.RegularExpressions;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// A burst of "[HH:mm:ss.fff] Render Requested: <item>" lines in the engine's rgl_log_<pid>.txt: the game
// asks to render every listed item's icon at once (an inventory or a cheat list holding hundreds of modded
// items). Evidence seen on one test instance: the session that ended in a native access violation logged
// 2,525 of them, 2,520 inside about one second, while the other sessions logged 0 to 1,063 over their whole
// run. What the log shows is that a burst preceded the end of that run, not that a burst causes a crash.
public sealed record RenderRequestBurst(int Count, string MostRequestedItem, string LogName, bool EndedAbnormally);

public sealed class RenderRequestBurstScanner
{
    // Well above the benign maximum of 1,063 render requests in a whole session, and below the 2,520 in
    // one second that preceded the crash, counted over any two consecutive seconds so a burst that
    // straddles a second boundary still counts.
    public const int BurstThreshold = 2000;

    private static readonly Regex Line = new(
        @"^\[(?<h>\d{2}):(?<m>\d{2}):(?<s>\d{2})(?:\.\d+)?\]\s+Render Requested:\s*(?<item>.*?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<long, int> perSecond = [];
    private readonly Dictionary<long, Dictionary<string, int>> items = [];
    private long day;
    private long lastOfDay = -1;
    private string? lastNonBlank;

    public void Add(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.Trim().Length > 0)
            lastNonBlank = line;

        var match = Line.Match(line);

        if (!match.Success)
            return;

        var ofDay = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) * 3600L
            + int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) * 60
            + int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);

        // The log stamps the time of day only; a drop of more than half a day is midnight passing.
        if (lastOfDay >= 0 && lastOfDay - ofDay > 43_200)
            day++;

        lastOfDay = ofDay;

        var second = (day * 86_400) + ofDay;
        perSecond[second] = perSecond.GetValueOrDefault(second) + 1;

        if (!items.TryGetValue(second, out var counts))
            items[second] = counts = new Dictionary<string, int>(StringComparer.Ordinal);

        var item = match.Groups["item"].Value;
        counts[item] = counts.GetValueOrDefault(item) + 1;
    }

    // The engine writes "Closing log file..." as the last line of a run that ended on its own; a log that
    // stops anywhere else (a dump message, a screen name) ended with the process.
    public bool EndedAbnormally =>
        lastNonBlank is null || !lastNonBlank.Contains("Closing log file", StringComparison.OrdinalIgnoreCase);

    public RenderRequestBurst? Result(string logName)
    {
        var bestCount = 0;
        var bestSecond = 0L;

        foreach (var second in perSecond.Keys)
        {
            var count = perSecond[second] + perSecond.GetValueOrDefault(second + 1);

            if (count > bestCount)
            {
                bestCount = count;
                bestSecond = second;
            }
        }

        if (bestCount < BurstThreshold)
            return null;

        var named = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var second in new[] { bestSecond, bestSecond + 1 })
        {
            if (!items.TryGetValue(second, out var counts))
                continue;

            foreach (var (item, n) in counts)
                named[item] = named.GetValueOrDefault(item) + n;
        }

        var top = named.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;

        return new RenderRequestBurst(bestCount, top, logName, EndedAbnormally);
    }

    public static RenderRequestBurst? Scan(string logText, string logName = "")
    {
        ArgumentNullException.ThrowIfNull(logText);

        var scanner = new RenderRequestBurstScanner();

        foreach (var line in logText.Split('\n'))
            scanner.Add(line.TrimEnd('\r'));

        return scanner.Result(logName);
    }

    // The worst burst among the newest logs: one that ended its run first, then the larger. Only the engine's
    // own rgl_log files carry these lines, and a log is skipped unread when it is too old or too large.
    public static RenderRequestBurst? ReadRecent(
        IEnumerable<DiscoveredLog> logs, DateTimeOffset now, TimeSpan? window = null, int maxFiles = 24, long maxBytesEach = 8_000_000)
    {
        ArgumentNullException.ThrowIfNull(logs);

        var since = now - (window ?? RecentRunErrors.DefaultWindow);
        RenderRequestBurst? worst = null;

        foreach (var log in logs.Where(l => l.Written >= since && l.SizeBytes <= maxBytesEach
                         && l.Name.StartsWith("rgl_log_", StringComparison.OrdinalIgnoreCase)
                         && !l.Name.StartsWith("rgl_log_errors", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(l => l.Written)
                     .Take(maxFiles))
        {
            try
            {
                using var stream = new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                var scanner = new RenderRequestBurstScanner();
                string? line;

                while ((line = reader.ReadLine()) is not null)
                    scanner.Add(line);

                var burst = scanner.Result(log.Name);

                if (burst is not null && (worst is null
                        || (burst.EndedAbnormally, burst.Count).CompareTo((worst.EndedAbnormally, worst.Count)) > 0))
                    worst = burst;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that is locked or gone is one fewer piece of evidence, never a failed launch check.
            }
        }

        return worst;
    }
}
