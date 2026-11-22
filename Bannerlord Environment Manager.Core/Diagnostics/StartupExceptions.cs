using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// One managed exception the Companion saw thrown before the game's first tick.
public sealed record StartupException(DateTime ThrownUtc, string Type, string Message, string ModuleFolder, IReadOnlyList<string> Frames)
{
    // The runtime's own "Type: message" line, the form RecentRunErrors keeps and LaunchEvidence.NamedBy
    // reads: "System.MissingMethodException: Method not found: '...'".
    public string Line => Type + ": " + Message;
}

// What the Companion's StartupExceptionLog wrote for one game process. A mod that kills the game while
// modules load leaves "Unhandled Exception Code 0xE0434352" in the engine's error log and nothing more:
// no message, no stack (measured on v1.4.8, E:\BemAgentData\res93g). This file is then the only place
// the exception's text exists. Folder and suffix must match the Companion's DryRunFiles.
public static class StartupExceptions
{
    public const string FolderName = "startup-exceptions";

    public const string Suffix = ".jsonl";

    public static string PathFor(string dataRoot, int processId) =>
        Path.Combine(dataRoot, FolderName, processId.ToString(CultureInfo.InvariantCulture) + Suffix);

    // The run's own file, by process id, and only if it was written during this run: a process id is
    // reused, and a file from an earlier process is not evidence about this one.
    public static IReadOnlyList<StartupException> FindForRun(string dataRoot, int processId, DateTime startedUtc)
    {
        if (string.IsNullOrWhiteSpace(dataRoot) || processId <= 0)
            return [];

        var path = PathFor(dataRoot, processId);

        try
        {
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < startedUtc.AddSeconds(-2))
                return [];

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            return Attributed(Parse(reader.ReadToEnd()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // One JSON object per line. The game can die mid write, so a broken last line is dropped, never
    // thrown over.
    public static IReadOnlyList<StartupException> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var records = new List<StartupException>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0)
                continue;

            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;

                if (root.ValueKind != JsonValueKind.Object || Text(root, "type") is not { Length: > 0 } type)
                    continue;

                var thrown = DateTime.TryParse(Text(root, "t"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                    ? t.ToUniversalTime()
                    : DateTime.MinValue;

                var frames = root.TryGetProperty("frames", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString()!).ToList()
                    : [];

                records.Add(new StartupException(thrown, type, Text(root, "message") ?? string.Empty, Text(root, "module") ?? string.Empty, frames));
            }
            catch (JsonException)
            {
                // A line cut off by the crash.
            }
        }

        return records;
    }

    // The game's own wrapper for a module that fails while modules load: InitializeSubModuleBases catches
    // the module's exception and throws a bare System.Exception from Module.Initialize, which carries no
    // message and no frame of the module, so its record names no module ("Exception of type
    // 'System.Exception' was thrown." with module empty, v1.5.7). The module is the one whose exception
    // was recorded just before it, the same module the wrapper replaced.
    public static IReadOnlyList<StartupException> Attributed(IReadOnlyList<StartupException> records)
    {
        var result = new List<StartupException>(records.Count);
        var lastModule = string.Empty;

        foreach (var record in records)
        {
            if (record.ModuleFolder.Length == 0 && IsBareModuleLoadFailure(record) && lastModule.Length > 0)
                result.Add(record with { ModuleFolder = lastModule });
            else
                result.Add(record);

            if (record.ModuleFolder.Length > 0)
                lastModule = record.ModuleFolder;
        }

        return result;
    }

    private static bool IsBareModuleLoadFailure(StartupException record) =>
        record.Type == "System.Exception"
        && record.Frames.Count > 0
        && record.Frames[0].StartsWith("TaleWorlds.MountAndBlade.Module.", StringComparison.Ordinal);

    // The record the crash is named after: the last one that belongs to a module, else the last one. The
    // Companion's own late records (its fault guard failing while the game is already going down) come
    // after the real one and name no module.
    public static StartupException? Culprit(IReadOnlyList<StartupException> records) =>
        records.LastOrDefault(r => r.ModuleFolder.Length > 0) ?? records.LastOrDefault();

    // The text LaunchEvidence.NamedBy reads, newest last as the runtime would have printed them.
    public static string CrashText(IEnumerable<StartupException> records) =>
        string.Join("\n", records.Select(r => r.Line));

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
