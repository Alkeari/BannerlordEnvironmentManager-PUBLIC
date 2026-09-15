using System.Text.Json;

namespace BannerlordEnvironmentManager.Core.DryRun;

// How precisely a failure can be pinned on a module. LoadLoop is what ships: the companion stands in
// for the engine's own submodule loading loop, so every module is called exactly as the engine calls
// it and a throw is observed around that call, with no mod's code rewritten. PerOverride is the
// earlier mechanism, a Harmony prefix and finalizer on each derived override, kept so an old result
// file still reads; it observed the same thing but ran every module's code as a Harmony-generated
// copy of itself, which obfuscated mods do not survive. BaseMethod is the fallback when the engine
// loop could not be stood in for: every override calls the base first, so a module is named as it
// starts but a throw inside it is not seen, and the culprit is inferred rather than observed.
public enum DryRunAttribution
{
    Unknown,
    PerOverride,
    BaseMethod,
    LoadLoop
}

public sealed record Breadcrumb(
    int Sequence,
    DateTime TimestampUtc,
    string Kind,
    string ModuleId = "",
    string TypeName = "",
    int? Index = null,
    long? DurationMs = null,
    string? ExceptionType = null,
    string? ExceptionMessage = null,
    int? SubModuleCount = null,
    string Granularity = "",
    string DegradedReason = "",
    string Phase = "",
    // Everything below is written by the patch watch, which records who is calling Harmony's own
    // patching entry points as they call them. Assembly is the assembly being scanned, HarmonyId is
    // the author-chosen id of the Harmony instance doing the scanning, PatchClass is the
    // [HarmonyPatch] class being applied at the moment something went wrong, and Target is the
    // single method a mod asked Harmony to patch by hand.
    string Assembly = "",
    string HarmonyId = "",
    string PatchClass = "",
    string Target = "",
    long? AtMs = null,
    string Mode = "")
{
    public string Label => ModuleId.Length > 0 ? ModuleId : TypeName;

    // The assembly is the fallback rather than the other way round: an assembly name is not a module
    // id, and reading it as one is how a report names the wrong mod.
    public string PatchLabel => ModuleId.Length > 0 ? ModuleId : Assembly.Length > 0 ? Assembly : HarmonyId;
}

public sealed record BreadcrumbTrail(
    IReadOnlyList<Breadcrumb> Records,
    int DiscardedLines,
    string? LoadingModule,
    int CompletedCount,
    int? SubModuleCount,
    DryRunAttribution Attribution,
    string DegradedReason,
    IReadOnlyList<string> FailedModules,
    string LastPhase,
    IReadOnlyList<DryRunModuleOutcome>? ObservedFailures = null,
    // The scan that started and never finished. This is the line that would have answered a crash
    // inside Harmony.PatchAll without any dump at all.
    Breadcrumb? UnfinishedPatchScan = null,
    int PatchScanCount = 0,
    string Mode = "",
    // Written only by CompleteWatch/Complete on the way out, so its absence in a Watch-mode trail
    // that otherwise looks clean is itself the finding: a session that closes normally always says
    // so, and one that does not is either still running, was closed by force, or crashed somewhere
    // the companion could not see.
    bool ExitRecorded = false,
    // Watch mode only: a periodic, unconditional "still running" marker written whether or not
    // anything else changed. Before this existed, a session that never got a new Harmony patch and
    // never threw left nothing recorded between the first tick and however it ended, so a silent
    // hang and a game the player was still happily playing looked identical to the trail.
    int HeartbeatCount = 0)
{
    public static BreadcrumbTrail Empty { get; } =
        new([], 0, null, 0, null, DryRunAttribution.Unknown, string.Empty, [], string.Empty);

    public bool IsDegraded => Attribution == DryRunAttribution.BaseMethod;

    // A throw record carries the exception the companion caught inside the module's own body. That is
    // observation, not the inference LoadingModule offers, and the two must never be reported alike.
    public IReadOnlyList<DryRunModuleOutcome> Failures => ObservedFailures ?? [];
}

public static class BreadcrumbFile
{
    // A half written final line is the normal case, not the exceptional one: the game dying mid load
    // is exactly what a dry run exists to catch. The incomplete line is counted and dropped, and
    // everything before it still answers which module was loading when the process went away.
    public static BreadcrumbTrail Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return BreadcrumbTrail.Empty;

        var records = new List<Breadcrumb>();
        var discarded = 0;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim('\r', ' ', '\t');

            if (trimmed.Length == 0)
                continue;

            if (TryRead(trimmed) is { } record)
                records.Add(record);
            else
                discarded++;
        }

        return Summarize(records, discarded);
    }

    public static BreadcrumbTrail Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return BreadcrumbTrail.Empty;

            // The game still holds the file open while it writes, so the share mode has to match the
            // one the companion opened it with.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BreadcrumbTrail.Empty;
        }
    }

    private static BreadcrumbTrail Summarize(List<Breadcrumb> records, int discarded)
    {
        string? loading = null;
        var completed = 0;
        int? subModuleCount = null;
        var attribution = DryRunAttribution.Unknown;
        var degradedReason = string.Empty;
        var failed = new List<string>();
        var observed = new List<DryRunModuleOutcome>();
        var lastPhase = string.Empty;
        Breadcrumb? scanning = null;
        var scans = 0;
        var mode = string.Empty;
        var exitRecorded = false;
        var heartbeats = 0;

        foreach (var record in records)
        {
            switch (record.Kind)
            {
                case "run-start":
                    mode = record.Mode;
                    break;
                case "exit":
                    exitRecorded = true;
                    break;
                case "heartbeat":
                    heartbeats++;
                    break;
                case "patch-scan-begin":
                    scanning = record;
                    scans++;
                    break;
                case "patch-scan-end":
                case "patch-scan-throw":
                    scanning = null;
                    break;
                case "discovery":
                    subModuleCount = record.SubModuleCount;
                    attribution = ReadAttribution(record.Granularity);
                    degradedReason = record.DegradedReason;
                    break;
                case "begin":
                    loading = record.Label;
                    break;
                case "end":
                    completed++;
                    loading = null;
                    break;
                case "throw":
                    completed++;
                    loading = null;
                    failed.Add(record.Label);
                    observed.Add(new DryRunModuleOutcome(
                        record.ModuleId,
                        record.TypeName,
                        record.Index ?? 0,
                        DryRunModuleStatus.Threw,
                        record.DurationMs,
                        string.Empty,
                        record.ExceptionType,
                        record.ExceptionMessage));
                    break;
                case "phase":
                    lastPhase = record.Phase;
                    break;
            }
        }

        return new BreadcrumbTrail(
            records, discarded, loading, completed, subModuleCount, attribution, degradedReason, failed, lastPhase,
            observed, scanning, scans, mode, exitRecorded, heartbeats);
    }

    public static DryRunAttribution ReadAttribution(string? granularity) => granularity switch
    {
        "LoadLoop" => DryRunAttribution.LoadLoop,
        "PerOverride" => DryRunAttribution.PerOverride,
        "BaseMethod" => DryRunAttribution.BaseMethod,
        _ => DryRunAttribution.Unknown
    };

    private static Breadcrumb? TryRead(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var kind = Text(root, "kind");

            if (kind.Length == 0)
                return null;

            return new Breadcrumb(
                Number(root, "seq") is { } seq ? (int)seq : 0,
                Time(root, "t"),
                kind,
                Text(root, "module"),
                Text(root, "type"),
                Number(root, "index") is { } index ? (int)index : null,
                Number(root, "durationMs"),
                Optional(root, "exceptionType"),
                Optional(root, "exceptionMessage"),
                Number(root, "subModuleCount") is { } count ? (int)count : null,
                Text(root, "granularity"),
                Text(root, "degradedReason"),
                Text(root, "phase"),
                Text(root, "assembly"),
                Text(root, "harmonyId"),
                Text(root, "patchClass"),
                Text(root, "target"),
                Number(root, "atMs"),
                Text(root, "mode"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string? Optional(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
            ? number
            : null;

    private static DateTime Time(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTime.TryParse(
                value.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : default;
}
