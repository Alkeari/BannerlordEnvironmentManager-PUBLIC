using System.Text.RegularExpressions;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// A module data file whose schema validation failed in the logged run.
public sealed record SchemaRejectedFile(string ModuleFolder, string File, string Schema, int Errors, string FirstMessage)
{
    // The element a schema holds: EquipmentRosters.xsd holds EquipmentRoster, NPCCharacters.xsd NPCCharacter.
    public string ElementName
    {
        get
        {
            var name = Schema.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase) ? Schema[..^4] : Schema;

            return name.EndsWith('s') ? name[..^1] : name;
        }
    }
}

// A module named as the cause of objects the engine never registered, and what it did.
public sealed record DataLoadCause(
    string ModuleFolder,
    string File,
    bool RemovesByStylesheet,
    int Errors,
    string FirstMessage,
    IReadOnlyList<string> MissingIds);

// The run log's "Null object reference found with ID" lines say the game asked for objects (default
// equipment rosters, core characters) that were never registered. The log names no module, so the cause
// is read from the same log and the module files: a module whose data file of the same kind as the
// missing objects failed schema validation (the engine drops what it cannot validate), or a module
// stylesheet that removes the object by its id.
public static partial class EngineDataLoadFailures
{
    public static IReadOnlyList<SchemaRejectedFile> RejectedFiles(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var found = new List<SchemaRejectedFile>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (!line.StartsWith(EngineDataLoadLog.SchemaMarker, StringComparison.Ordinal))
                continue;

            var parts = line[EngineDataLoadLog.SchemaMarker.Length..].Split('|', 5);

            if (parts.Length == 5 && int.TryParse(parts[3], out var count)
                && !found.Any(f => f.ModuleFolder == parts[0] && f.File == parts[1]))
                found.Add(new SchemaRejectedFile(parts[0], parts[1], parts[2], count, parts[4]));
        }

        return found;
    }

    public static IReadOnlyList<string> MissingIds(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in text.Split('\n'))
        {
            var at = raw.IndexOf(EngineDataLoadLog.NullMarker, StringComparison.Ordinal);

            if (at < 0)
                continue;

            var id = raw[(at + EngineDataLoadLog.NullMarker.Length)..].Trim();

            if (id.Length > 0 && seen.Add(id))
                ids.Add(id);
        }

        return ids;
    }

    [GeneratedRegex(@"<(?<element>[A-Za-z_]\w*)\s[^>]*?\bid\s*=\s*""(?<id>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex Definition { get; }

    [GeneratedRegex(@"<xsl:template\s+match\s*=\s*""(?<match>[^""]*)""\s*/>", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyTemplate { get; }

    // moduleDataByFolder maps a module folder name to its ModuleData directory for every enabled module;
    // the official ones are in it too, since the missing rosters are defined there, and are never named.
    public static IReadOnlyList<DataLoadCause> Find(
        string text, IReadOnlyDictionary<string, string> moduleDataByFolder, ISet<string> officialFolders)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(moduleDataByFolder);
        ArgumentNullException.ThrowIfNull(officialFolders);

        var missing = MissingIds(text);

        if (missing.Count == 0)
            return [];

        var wanted = new HashSet<string>(missing, StringComparer.Ordinal);
        var elementById = ElementsOf(wanted, moduleDataByFolder);
        var causes = new List<DataLoadCause>();

        foreach (var rejected in RejectedFiles(text))
        {
            if (officialFolders.Contains(rejected.ModuleFolder) || !moduleDataByFolder.ContainsKey(rejected.ModuleFolder))
                continue;

            var ids = missing.Where(id => elementById.TryGetValue(id, out var element)
                && string.Equals(element, rejected.ElementName, StringComparison.Ordinal)).ToList();

            if (ids.Count > 0)
                causes.Add(new DataLoadCause(rejected.ModuleFolder, rejected.File, false, rejected.Errors, rejected.FirstMessage, ids));
        }

        foreach (var (folder, dataDir) in moduleDataByFolder)
        {
            if (officialFolders.Contains(folder) || !Directory.Exists(dataDir))
                continue;

            foreach (var sheet in EnumerateSafe(dataDir, "*.xslt"))
            {
                var removed = RemovedIds(sheet, wanted);

                if (removed.Count > 0)
                    causes.Add(new DataLoadCause(folder, Path.GetFileName(sheet), true, 0, string.Empty, removed));
            }
        }

        return causes;
    }

    private static List<string> RemovedIds(string sheet, HashSet<string> wanted)
    {
        string content;

        try
        {
            if (new FileInfo(sheet).Length > 2_000_000)
                return [];

            content = File.ReadAllText(sheet);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var removed = new List<string>();

        foreach (Match empty in EmptyTemplate.Matches(content))
        {
            var pattern = empty.Groups["match"].Value;

            foreach (var id in wanted)
            {
                if ((pattern.Contains($"'{id}'", StringComparison.Ordinal) || pattern.Contains($"&quot;{id}&quot;", StringComparison.Ordinal))
                    && !removed.Contains(id))
                    removed.Add(id);
            }
        }

        return removed;
    }

    private static Dictionary<string, string> ElementsOf(HashSet<string> wanted, IReadOnlyDictionary<string, string> moduleDataByFolder)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var dataDir in moduleDataByFolder.Values)
        {
            if (!Directory.Exists(dataDir))
                continue;

            foreach (var path in EnumerateSafe(dataDir, "*.xml"))
            {
                if (map.Count == wanted.Count)
                    return map;

                try
                {
                    if (new FileInfo(path).Length > 40_000_000)
                        continue;

                    foreach (Match match in Definition.Matches(File.ReadAllText(path)))
                    {
                        var id = match.Groups["id"].Value;

                        if (wanted.Contains(id))
                            map.TryAdd(id, match.Groups["element"].Value);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file that is locked or gone leaves its objects untyped, so none is blamed on it.
                }
            }
        }

        return map;
    }

    private static List<string> EnumerateSafe(string dir, string pattern)
    {
        try
        {
            return [.. Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
