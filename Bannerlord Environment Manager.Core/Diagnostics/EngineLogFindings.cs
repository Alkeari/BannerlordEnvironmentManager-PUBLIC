using System.Text.RegularExpressions;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// One module data file the game's own schema check rejected while it was loading: the module folder, the
// file inside it, the schema it was checked against (EquipmentRosters.xsd) and how many errors it drew.
public sealed record SchemaFileErrors(string ModuleFolder, string File, string Schema, int ErrorCount, string FirstError);

// What the engine's log (rgl_log_<pid>.txt) says about the load that crashed: which data files failed the
// schema, and which object ids the game printed as "Null object reference" when it dropped everything
// that never finished loading.
public sealed record EngineLogFindings(IReadOnlyList<SchemaFileErrors> SchemaErrors, IReadOnlyList<string> NullIds)
{
    public static EngineLogFindings Empty { get; } = new([], []);

    public bool IsEmpty => SchemaErrors.Count == 0 && NullIds.Count == 0;
}

public static partial class EngineLog
{
    [GeneratedRegex(@"^\[[\d:.]+\]\s+opening\s+(?<path>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Opening();

    [GeneratedRegex(@"^\[[\d:.]+\]\s+Error:\s*(?<text>.*?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"^\[[\d:.]+\]\s+Null object reference found with ID:\s*(?<id>\S+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex NullReference();

    [GeneratedRegex(@"^\[[\d:.]+\]", RegexOptions.CultureInvariant)]
    private static partial Regex Stamped();

    [GeneratedRegex(@"[\\/]Modules[\\/](?<module>[^\\/]+)[\\/](?<file>.+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ModuleFile();

    public static EngineLogFindings ReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            return Parse(ReadLines(reader));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return EngineLogFindings.Empty;
        }
    }

    // The log of the run with this id (the game's process id ends it), or empty when it is not there.
    public static EngineLogFindings ReadRun(string logsFolder, string? runId)
    {
        var processId = BannerlordEnvironmentManager.Core.DryRun.ExceptionReport.ProcessIdOf(runId);

        if (processId == 0 || !Directory.Exists(logsFolder))
            return EngineLogFindings.Empty;

        var path = Path.Combine(logsFolder, $"rgl_log_{processId}.txt");

        return File.Exists(path) ? ReadFile(path) : EngineLogFindings.Empty;
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    // An error line belongs to the last data file opened before it; a schema opened in between only names
    // the schema that file is checked against. A file opened again starts a later load, which replaces the
    // earlier one: the log keeps every attempt of a process and the crash is the last.
    public static EngineLogFindings Parse(IEnumerable<string> lines)
    {
        var files = new Dictionary<string, (string Module, string File, string Schema, int Count, string First)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var nulls = new List<string>();
        var nullSeen = new HashSet<string>(StringComparer.Ordinal);
        string? current = null;
        var awaitingSchema = false;

        foreach (var line in lines)
        {
            if (Opening().Match(line) is { Success: true } open)
            {
                var opened = open.Groups["path"].Value;

                if (opened.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase))
                {
                    if (awaitingSchema && current is not null && files.TryGetValue(current, out var f))
                        files[current] = f with { Schema = Path.GetFileName(opened.Replace('\\', '/')) };

                    awaitingSchema = false;
                    continue;
                }

                awaitingSchema = false;

                if (files.ContainsKey(opened))
                {
                    files.Clear();
                    order.Clear();
                    nulls.Clear();
                    nullSeen.Clear();
                }

                current = null;

                if (ModuleFile().Match(opened) is { Success: true } m)
                {
                    current = opened;
                    awaitingSchema = true;
                    files[opened] = (m.Groups["module"].Value, m.Groups["file"].Value.Replace('\\', '/'), string.Empty, 0, string.Empty);
                    order.Add(opened);
                }

                continue;
            }

            if (ErrorLine().Match(line) is { Success: true } err)
            {
                if (current is not null && files.TryGetValue(current, out var f))
                    files[current] = f with { Count = f.Count + 1, First = f.Count == 0 ? err.Groups["text"].Value : f.First };

                continue;
            }

            if (NullReference().Match(line) is { Success: true } nul)
            {
                current = null;

                if (nullSeen.Add(nul.Groups["id"].Value))
                    nulls.Add(nul.Groups["id"].Value);

                continue;
            }

            // Continuation lines (Node, Line, XML Path) carry no stamp and keep the file; any other
            // stamped line ends it.
            if (Stamped().IsMatch(line))
            {
                current = null;
                awaitingSchema = false;
            }
        }

        return new EngineLogFindings(
            [.. order.Select(o => files[o]).Where(f => f.Count > 0)
                .Select(f => new SchemaFileErrors(f.Module, f.File, f.Schema, f.Count, f.First))],
            nulls);
    }
}
