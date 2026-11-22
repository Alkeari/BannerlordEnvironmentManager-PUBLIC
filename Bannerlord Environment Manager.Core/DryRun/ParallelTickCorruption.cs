using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.DryRun;

// The game moves parties on several threads at once (CampaignTickCacheDataStore.ParallelTickMovingParties
// and the other ParallelTick methods). A mod that writes a plain Dictionary, HashSet or List from code
// those threads run corrupts it: Dictionary.Insert throws IndexOutOfRangeException, later reads throw
// NullReferenceException or loop, and sooner or later one escapes and the game dies. Seen 2026-11-09:
// MapBlockade's BlockadeReachabilityCache threw 316 of these from ParallelTickMovingParties over half an
// hour, and the game died 190 ms after the last one. The companion's first-chance trace keeps each
// throw with its module and frames, so the run before the crash already says which mod is doing it.
public sealed record ParallelTickHit(ModuleId ModuleId, string ExceptionType, string Frame, int Count);

public static class ParallelTickCorruption
{
    private static readonly string[] CollectionFrames =
    [
        "System.Collections.Generic.Dictionary`2.",
        "System.Collections.Generic.HashSet`1.",
        "System.Collections.Generic.List`1.",
        "System.Collections.Generic.Queue`1.",
    ];

    private static readonly string[] Corruption =
    [
        "System.IndexOutOfRangeException",
        "System.ArgumentOutOfRangeException",
        "System.NullReferenceException",
        "System.InvalidOperationException",
        "System.ArgumentException",
    ];

    public static bool IsHit(FirstChanceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return !record.Module.IsEmpty
            && record.Frames.Count > 0
            && Corruption.Contains(record.ExceptionType, StringComparer.Ordinal)
            && CollectionFrames.Any(c => record.Frames[0].StartsWith(c, StringComparison.Ordinal))
            && record.Frames.Any(f => f.Contains(".ParallelTick", StringComparison.Ordinal)
                || f.Contains("TWParallel", StringComparison.Ordinal));
    }

    // One hit per module, counting repeats, the most frequent first.
    public static IReadOnlyList<ParallelTickHit> Find(IEnumerable<FirstChanceTrace> traces)
    {
        ArgumentNullException.ThrowIfNull(traces);

        return
        [
            .. traces.SelectMany(t => t.Records)
                .Where(IsHit)
                .GroupBy(r => r.Module)
                .Select(g =>
                {
                    var first = g.First();
                    var own = first.Frames.FirstOrDefault(f => !f.StartsWith("System.", StringComparison.Ordinal)) ?? first.Frames[0];
                    return new ParallelTickHit(g.Key, first.ExceptionType, own, g.Sum(r => Math.Max(1, r.Repeats)));
                })
                .OrderByDescending(h => h.Count)
                .ThenBy(h => h.ModuleId.Value, StringComparer.OrdinalIgnoreCase)
        ];
    }

    // The traces written within the window, newest first, at most maxFiles of them.
    public static IReadOnlyList<FirstChanceTrace> ReadRecent(string root, DateTimeOffset now, TimeSpan? window = null, int maxFiles = 12)
    {
        try
        {
            if (!Directory.Exists(root))
                return [];

            var since = (now - (window ?? TimeSpan.FromDays(7))).UtcDateTime;

            return
            [
                .. Directory.EnumerateFiles(root, "*" + FirstChancePaths.Suffix)
                    .Select(p => (Path: p, Written: File.GetLastWriteTimeUtc(p)))
                    .Where(f => f.Written >= since)
                    .OrderByDescending(f => f.Written)
                    .Take(maxFiles)
                    .Select(f => FirstChanceTrace.Read(f.Path))
                    .OfType<FirstChanceTrace>()
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
