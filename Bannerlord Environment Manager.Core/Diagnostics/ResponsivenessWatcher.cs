using System.Globalization;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

/// <summary>
/// Logs how long the UI thread takes to run a posted no-op. <see cref="Tick"/> is called a few times a
/// second from a background thread; every <see cref="SampleIntervalMs"/> it posts a probe, and the probe's
/// latency is logged when over <see cref="LogThresholdMs"/>. A probe unanswered for <see cref="HangMs"/>
/// is a hang: <c>dump</c> is called once for it, and its full length is logged when it ends. A summary
/// (count, max, p95) is logged every <see cref="SummaryInterval"/>.
/// </summary>
public sealed class ResponsivenessWatcher
{
    public const int SampleIntervalMs = 2000;
    public const int LogThresholdMs = 250;
    public const int HangMs = 2000;
    public static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(5);

    private readonly Func<long> nowMs;
    private readonly Action<Action> postToUi;
    private readonly Action<string> log;
    private readonly Action<string> dump;
    private readonly Func<DateTime> clock;
    private readonly object gate = new();
    private readonly List<long> window = [];
    private long pendingSince = -1;
    private long lastPostAt = long.MinValue / 2;
    private long summaryDueAt;
    private bool hung;
    private int hangCount;

    public ResponsivenessWatcher(Func<long> nowMs, Action<Action> postToUi, Action<string> log, Action<string> dump, Func<DateTime>? clock = null)
    {
        this.nowMs = nowMs;
        this.postToUi = postToUi;
        this.log = log;
        this.dump = dump;
        this.clock = clock ?? (() => DateTime.Now);
        summaryDueAt = nowMs() + (long)SummaryInterval.TotalMilliseconds;
    }

    public void Tick()
    {
        var lines = new List<string>();
        string? dumpTag = null;
        var post = false;

        lock (gate)
        {
            var now = nowMs();

            if (pendingSince < 0)
            {
                if (now - lastPostAt >= SampleIntervalMs)
                {
                    pendingSince = now;
                    lastPostAt = now;
                    post = true;
                }
            }
            else if (!hung && now - pendingSince >= HangMs)
            {
                hung = true;
                hangCount++;
                dumpTag = $"hang{hangCount}";
                lines.Add($"{Stamp()}\tHANG\tno answer after {now - pendingSince} ms; dump {dumpTag}");
            }

            if (now >= summaryDueAt)
            {
                summaryDueAt = now + (long)SummaryInterval.TotalMilliseconds;
                lines.Add(Summarize());
            }
        }

        foreach (var line in lines)
            log(line);

        if (dumpTag is not null)
            dump(dumpTag);

        if (post)
            postToUi(Beat);
    }

    /// <summary>Runs on the UI thread when the probe is finally handled.</summary>
    public void Beat()
    {
        string? line = null;

        lock (gate)
        {
            if (pendingSince < 0)
                return;

            var latency = nowMs() - pendingSince;
            window.Add(latency);

            if (hung)
                line = $"{Stamp()}\tHANG-END\t{latency} ms in total";
            else if (latency > LogThresholdMs)
                line = $"{Stamp()}\t{latency}";

            hung = false;
            pendingSince = -1;
        }

        if (line is not null)
            log(line);
    }

    private string Stamp() => clock().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private string Summarize()
    {
        var count = window.Count;
        if (count == 0)
            return $"{Stamp()}\tSUMMARY\tcount 0";

        var sorted = window.OrderBy(v => v).ToList();
        var p95 = sorted[Math.Min(count - 1, (int)Math.Ceiling(count * 0.95) - 1)];
        window.Clear();
        return $"{Stamp()}\tSUMMARY\tcount {count}, max {sorted[^1]} ms, p95 {p95} ms";
    }
}

/// <summary>Keeps a hangs folder to its newest few dumps.</summary>
public static class HangDumpRetention
{
    public const int MaxKept = 10;

    public static void Prune(string folder, int keep = MaxKept)
    {
        if (!Directory.Exists(folder))
            return;

        var stale = new DirectoryInfo(folder).GetFiles("*.dmp")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(keep);

        foreach (var file in stale)
        {
            try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
