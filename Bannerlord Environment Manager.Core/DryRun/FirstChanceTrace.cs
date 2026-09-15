using System.Globalization;
using System.Text.Json;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.DryRun;

public enum FirstChanceClass
{
    Unknown,
    Infrastructure,
    GameOnly,
    PatchedGame,
    ModOrigin
}

public sealed record FirstChanceCount(string ExceptionType, int Count);

public sealed record FirstChanceRecord(
    int Sequence,
    DateTime WhenUtc,
    string ExceptionType,
    string Message,
    FirstChanceClass Classification,
    ModuleId Module,
    IReadOnlyList<string> Frames,
    int Repeats,
    // What the throwing thread was doing to Harmony at that instant, recorded by the companion's
    // patch watch. Empty means the thread was not patching, which is a measurement and not a gap.
    ModuleId PatchingModule = default,
    string PatchingActivity = "")
{
    public bool ThrownWhilePatching => PatchingActivity.Length > 0;

    public string Describe()
    {
        var where = Frames.Count > 0
            ? Strings.Current.Format("Core.DryRun.FirstChance.Describe.ThrownIn", Frames[0])
            : string.Empty;
        var who = Module.IsEmpty
            ? string.Empty
            : Strings.Current.Format("Core.DryRun.FirstChance.Describe.OwnedBy", Module);
        var again = Repeats > 1
            ? Strings.Current.Format("Core.DryRun.FirstChance.Describe.Repeats", Repeats)
            : string.Empty;
        var patching = PatchingActivity.Length == 0
            ? string.Empty
            : PatchingModule.IsEmpty
                ? Strings.Current.Format("Core.DryRun.FirstChance.Describe.PatchingActivity", PatchingActivity)
                : Strings.Current.Format(
                    "Core.DryRun.FirstChance.Describe.PatchingModuleActivity", PatchingModule, PatchingActivity);

        return $"{WhenUtc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {ExceptionType}{where}{who}"
            + $"{patching}{again}: {Message}";
    }
}

public static class FirstChancePaths
{
    public const string Suffix = ".firstchance.json";

    public static string GetDefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        "first-chance");

    public static string TracePath(string root, string runId) => Path.Combine(root, runId + Suffix);
}

// The companion's ring buffer, read back. Everything here is inference vocabulary: an exception that
// was thrown and handled is a lead, never a cause. Only a bisection may use the stronger word.
public sealed record FirstChanceTrace(
    int Schema,
    string RunId,
    string Mode,
    DateTime StartedUtc,
    DateTime FlushedUtc,
    int Observed,
    int Filtered,
    int Captured,
    int Dropped,
    int Capacity,
    bool Wrapped,
    bool Disabled,
    string DisabledReason,
    IReadOnlyList<FirstChanceCount> Counts,
    IReadOnlyList<FirstChanceRecord> Records)
{
    // Everything an exception left behind is still there a few seconds later, and everything from a
    // minute ago has usually been overwritten by the game itself. Five seconds is the research's
    // estimate and it is an estimate, so it is a parameter rather than a constant.
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(5);

    public static FirstChanceTrace? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            return new FirstChanceTrace(
                (int)(Number(root, "schema") ?? 0),
                Text(root, "runId"),
                Text(root, "mode"),
                Time(root, "startedUtc"),
                Time(root, "flushedUtc"),
                (int)(Number(root, "observed") ?? 0),
                (int)(Number(root, "filtered") ?? 0),
                (int)(Number(root, "captured") ?? 0),
                (int)(Number(root, "dropped") ?? 0),
                (int)(Number(root, "capacity") ?? 0),
                Flag(root, "wrapped"),
                Flag(root, "disabled"),
                Text(root, "disabledReason"),
                ReadCounts(root),
                ReadRecords(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static FirstChanceTrace? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static FirstChanceTrace? ReadNewest(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return null;

            var newest = Directory.EnumerateFiles(root, "*" + FirstChancePaths.Suffix)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            return newest is null ? null : Read(newest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Newest first: what happened immediately before the crash is what matters, and the reader
    // should meet it first.
    public IReadOnlyList<FirstChanceRecord> SwallowedBefore(DateTime whenUtc, TimeSpan? window = null)
    {
        var span = window ?? DefaultWindow;

        return
        [
            .. Records
                .Where(r => r.WhenUtc <= whenUtc && whenUtc - r.WhenUtc <= span)
                .OrderByDescending(r => r.WhenUtc)
                .ThenByDescending(r => r.Sequence)
        ];
    }

    public string Explain()
    {
        var lines = new List<string>();

        if (Observed == 0)
        {
            lines.Add(Strings.Current["Core.DryRun.FirstChance.Explain.NoneObserved"]);
        }
        else
        {
            lines.Add(Strings.Current.Plural(
                "Core.DryRun.FirstChance.Explain.Observed", Observed, Filtered, Captured, Dropped));
        }

        var owners = Records
            .Where(r => !r.Module.IsEmpty)
            .GroupBy(r => r.Module)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (owners.Count > 0)
        {
            lines.Add(Strings.Current.Format(
                "Core.DryRun.FirstChance.Explain.Owners",
                string.Join(", ", owners.Select(g => $"{g.Key} ({g.Count()})"))));
        }

        // The line that answers a crash inside Harmony.PatchAll. A dump of that crash names Harmony
        // and the game and nothing in between; this names the mod that was patching at that instant.
        var patching = Records.Where(r => r.ThrownWhilePatching).ToList();

        if (patching.Count > 0)
        {
            var byModule = patching
                .Where(r => !r.PatchingModule.IsEmpty)
                .GroupBy(r => r.PatchingModule)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();

            lines.Add(Strings.Current.Plural("Core.DryRun.FirstChance.Explain.Patching.Count", patching.Count)
                + (byModule.Count == 0
                    ? Strings.Current["Core.DryRun.FirstChance.Explain.Patching.NoModule"]
                    : Strings.Current.Format(
                        "Core.DryRun.FirstChance.Explain.Patching.ByModule",
                        string.Join(", ", byModule.Select(g => $"{g.Key} ({g.Count()})"))))
                + Strings.Current["Core.DryRun.FirstChance.Explain.Patching.Tail"]);
        }

        if (Wrapped)
        {
            lines.Add(Strings.Current.Plural("Core.DryRun.FirstChance.Explain.Wrapped", Capacity));
        }

        if (Disabled)
        {
            lines.Add(Strings.Current.Format("Core.DryRun.FirstChance.Explain.Disabled", DisabledReason));
        }

        return string.Join(" ", lines);
    }

    private static IReadOnlyList<FirstChanceCount> ReadCounts(JsonElement root)
    {
        if (!root.TryGetProperty("counts", out var counts) || counts.ValueKind != JsonValueKind.Array)
            return [];

        return
        [
            .. counts.EnumerateArray()
                .Where(c => c.ValueKind == JsonValueKind.Object)
                .Select(c => new FirstChanceCount(Text(c, "type"), (int)(Number(c, "count") ?? 0)))
        ];
    }

    private static IReadOnlyList<FirstChanceRecord> ReadRecords(JsonElement root)
    {
        if (!root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            return [];

        var read = new List<FirstChanceRecord>();

        foreach (var record in records.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object)
                continue;

            read.Add(new FirstChanceRecord(
                (int)(Number(record, "seq") ?? 0),
                Time(record, "t"),
                Text(record, "type"),
                Text(record, "message"),
                ReadClass(Text(record, "class")),
                new ModuleId(Text(record, "module")),
                ReadFrames(record),
                (int)(Number(record, "repeats") ?? 1),
                new ModuleId(Text(record, "patchModule")),
                Text(record, "patchActivity")));
        }

        return read;
    }

    private static IReadOnlyList<string> ReadFrames(JsonElement record)
    {
        if (!record.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array)
            return [];

        return
        [
            .. frames.EnumerateArray()
                .Where(f => f.ValueKind == JsonValueKind.String)
                .Select(f => f.GetString() ?? string.Empty)
        ];
    }

    // A word this build does not know is a newer companion, not a corrupt file, and Unknown is the
    // reading that claims the least.
    private static FirstChanceClass ReadClass(string text) =>
        Enum.TryParse<FirstChanceClass>(text, ignoreCase: true, out var value)
            ? value
            : FirstChanceClass.Unknown;

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
            ? number
            : null;

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTime Time(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTime.TryParse(
                value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed)
            ? parsed
            : default;
}
