using System.Text.Json;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.DryRun;

public enum DryRunModuleStatus
{
    NotObserved,
    NoOverride,
    Loading,
    Loaded,
    Threw
}

public sealed record DryRunModuleOutcome(
    string Id,
    string TypeName,
    int Index,
    DryRunModuleStatus Status,
    long? DurationMs = null,
    string Assembly = "",
    string? ExceptionType = null,
    string? ExceptionMessage = null,
    string? ExceptionStack = null)
{
    public string Label => Id.Length > 0 ? Id : TypeName;
}

public sealed record DryRunResult(
    int Schema,
    string RunId,
    string CompanionVersion,
    DateTime StartedUtc,
    DateTime CompletedUtc,
    long ElapsedMs,
    string GameVersion,
    // The load-bearing measurement in the whole design. A full module count means per-override
    // patching worked; one means only the companion was in the collection and attribution degraded.
    int SubModuleCount,
    int CompanionIndex,
    DryRunAttribution Attribution,
    bool IsDegraded,
    string DegradedReason,
    int PatchedOverrides,
    int SkippedNoOverride,
    int PatchFailures,
    bool ReachedFirstTick,
    bool Booted,
    IReadOnlyList<DryRunModuleOutcome> Modules,
    IReadOnlyList<string> CompanionErrors,
    // Captured at the first application tick from Harmony itself, which is the only place the truth
    // exists: a static scan misses every patch created from a computed method reference.
    IReadOnlyList<PatchRecord>? Patches = null,
    int PatchedMethodCount = 0,
    int UnattributedPatches = 0)
{
    public IReadOnlyList<DryRunModuleOutcome> FailedModules =>
        [.. Modules.Where(m => m.Status == DryRunModuleStatus.Threw)];
}

// A registry is only meaningful beside the module set it was captured from, so the two are bound
// together here rather than left for a caller to pair up correctly.
public static class PatchRegistryCapture
{
    public static PatchRegistry? From(DryRunResult? result, IReadOnlyList<ModuleId> moduleSet)
    {
        if (result is null || !result.ReachedFirstTick)
            return null;

        return new PatchRegistry(
            result.Patches ?? [],
            moduleSet ?? [],
            string.IsNullOrWhiteSpace(result.GameVersion) ? null : result.GameVersion,
            result.CompletedUtc == default ? DateTime.UtcNow : result.CompletedUtc,
            result.RunId,
            result.PatchedMethodCount);
    }
}

public static class DryRunResultFile
{
    // Null rather than an exception for anything unreadable: a result file written by a process that
    // was killed is a normal outcome, and the breadcrumb trail still has to be reported.
    public static DryRunResult? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            return new DryRunResult(
                (int)(Number(root, "schema") ?? 0),
                Text(root, "runId"),
                Text(root, "companionVersion"),
                Time(root, "startedUtc"),
                Time(root, "completedUtc"),
                Number(root, "elapsedMs") ?? 0,
                Text(root, "gameVersion"),
                (int)(Number(root, "subModuleCount") ?? 0),
                (int)(Number(root, "companionIndex") ?? -1),
                BreadcrumbFile.ReadAttribution(Text(root, "granularity")),
                Flag(root, "isDegraded"),
                Text(root, "degradedReason"),
                (int)(Number(root, "patchedOverrides") ?? 0),
                (int)(Number(root, "skippedNoOverride") ?? 0),
                (int)(Number(root, "patchFailures") ?? 0),
                Flag(root, "reachedFirstTick"),
                Flag(root, "booted"),
                ReadModules(root),
                ReadErrors(root),
                ReadPatches(root, out var unattributed),
                (int)(Number(root, "patchedMethodCount") ?? 0),
                unattributed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static DryRunResult? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<DryRunModuleOutcome> ReadModules(JsonElement root)
    {
        if (!root.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array)
            return [];

        var outcomes = new List<DryRunModuleOutcome>();

        foreach (var module in modules.EnumerateArray())
        {
            if (module.ValueKind != JsonValueKind.Object)
                continue;

            outcomes.Add(new DryRunModuleOutcome(
                Text(module, "id"),
                Text(module, "type"),
                (int)(Number(module, "index") ?? 0),
                ReadStatus(Text(module, "status")),
                Number(module, "durationMs"),
                Text(module, "assembly"),
                Optional(module, "exceptionType"),
                Optional(module, "exceptionMessage"),
                Optional(module, "exceptionStack")));
        }

        return outcomes;
    }

    private static IReadOnlyList<PatchRecord> ReadPatches(JsonElement root, out int unattributed)
    {
        unattributed = 0;

        if (!root.TryGetProperty("patches", out var patches) || patches.ValueKind != JsonValueKind.Array)
            return [];

        var records = new List<PatchRecord>();

        foreach (var patch in patches.EnumerateArray())
        {
            if (patch.ValueKind != JsonValueKind.Object)
                continue;

            var attributedBy = ReadAttribution(Text(patch, "attributedBy"));
            var module = Text(patch, "module");

            if (attributedBy is PatchAttribution.Unattributed || module.Length == 0)
                unattributed++;

            records.Add(new PatchRecord(
                Text(patch, "type"),
                Text(patch, "method"),
                new ModuleId(module),
                ReadKind(Text(patch, "kind")),
                Text(patch, "owner"),
                attributedBy,
                Text(patch, "patchMethod")));
        }

        return records;
    }

    // A word this build does not know is a version skew with a newer companion, not a corrupt file.
    // Both fall back to the reading that claims the least.
    private static PatchKind ReadKind(string text) =>
        Enum.TryParse<PatchKind>(text, ignoreCase: true, out var kind) ? kind : PatchKind.Postfix;

    private static PatchAttribution ReadAttribution(string text) =>
        Enum.TryParse<PatchAttribution>(text, ignoreCase: true, out var attribution)
            ? attribution
            : PatchAttribution.Unattributed;

    private static IReadOnlyList<string> ReadErrors(JsonElement root)
    {
        if (!root.TryGetProperty("companionErrors", out var errors) || errors.ValueKind != JsonValueKind.Array)
            return [];

        return [.. errors.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? string.Empty)];
    }

    // An unrecognized word must not lose the rest of the file: a newer companion adding a status is
    // a version skew, not a corrupt result.
    private static DryRunModuleStatus ReadStatus(string text) =>
        Enum.TryParse<DryRunModuleStatus>(text, ignoreCase: true, out var status)
            ? status
            : DryRunModuleStatus.NotObserved;

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

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTime Time(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTime.TryParse(
                value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed)
            ? parsed
            : default;
}
