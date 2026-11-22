using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Modules;

// One node of the category tree the Environment filter shows. Count includes every module under the
// node, so picking a parent says how many rows it will keep.
public sealed record ModCategoryNode(string Name, string Path, int Count, IReadOnlyList<ModCategoryNode> Children);

// Categories as a tree rather than free tags: "Gameplay/Combat" sits under "Gameplay", as Vortex and
// Mod Organizer 2 list them. A module's own filing wins; with none, the category Nexus Mods files its
// page under stands in, so the tree is useful before anyone has filed anything.
public static class ModCategories
{
    public const char Separator = '/';

    public const int MaxDepth = 4;

    public const int MaxSegmentLength = 40;

    // "  Gameplay // Combat/ " becomes "Gameplay/Combat": segments trimmed, empties dropped, each capped,
    // depth capped. Empty text is no category.
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var segments = new List<string>();

        foreach (var raw in path.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segments.Count == MaxDepth)
                break;

            segments.Add(raw.Length > MaxSegmentLength ? raw[..MaxSegmentLength].TrimEnd() : raw);
        }

        return string.Join(Separator, segments);
    }

    public static string Effective(string? filed, string? nexusCategory)
    {
        var own = Normalize(filed);

        return own.Length > 0 ? own : Normalize(nexusCategory?.Replace(Separator, '-'));
    }

    // An empty filter keeps everything. A filter keeps its own category and every one below it, and
    // matches whole segments: "Game" does not keep "Gameplay".
    public static bool Matches(string? modulePath, string? filterPath)
    {
        var filter = Normalize(filterPath);

        if (filter.Length == 0)
            return true;

        var module = Normalize(modulePath);

        return module.Equals(filter, StringComparison.OrdinalIgnoreCase)
            || module.StartsWith(filter + Separator, StringComparison.OrdinalIgnoreCase);
    }

    // Nodes sort by name; spellings that differ only in case are one node, keeping the first spelling.
    // Modules with no category are not in the tree: the caller shows them as their own row using
    // Uncategorized.
    public static IReadOnlyList<ModCategoryNode> BuildTree(IEnumerable<string?> modulePaths)
    {
        ArgumentNullException.ThrowIfNull(modulePaths);

        var root = new Builder(string.Empty, string.Empty);

        foreach (var path in modulePaths.Select(Normalize).Where(path => path.Length > 0))
        {
            var node = root;

            foreach (var segment in path.Split(Separator))
            {
                node = node.Child(segment);
                node.Count++;
            }
        }

        return root.Freeze().Children;
    }

    public static int Uncategorized(IEnumerable<string?> modulePaths) =>
        modulePaths.Count(path => Normalize(path).Length == 0);

    private sealed class Builder(string name, string path)
    {
        private readonly Dictionary<string, Builder> children = new(StringComparer.OrdinalIgnoreCase);

        public string Label => name;

        public int Count { get; set; }

        public Builder Child(string segment)
        {
            if (!children.TryGetValue(segment, out var child))
                children[segment] = child = new Builder(segment, path.Length == 0 ? segment : path + Separator + segment);

            return child;
        }

        public ModCategoryNode Freeze() =>
            new(name, path, Count, [.. children.Values.OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase).Select(c => c.Freeze())]);
    }
}

// The category each module is filed under, per game version beside the notes and pins.
public sealed class ModCategoryStore(string? filePath)
{
    public const string FileName = "mod-categories.json";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string? FilePath { get; } = filePath;

    public static string DefaultPath(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, FileName);

    public IReadOnlyDictionary<string, string> Load()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
            return result;

        try
        {
            foreach (var (id, path) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? [])
            {
                var clean = ModCategories.Normalize(path);

                if (clean.Length > 0)
                    result[id] = clean;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    public string Get(string moduleId) => Load().TryGetValue(moduleId, out var path) ? path : string.Empty;

    // An empty category removes the filing, so a cleared box leaves nothing behind.
    public bool Set(string moduleId, string? path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);

        if (string.IsNullOrWhiteSpace(FilePath))
            return false;

        var all = new Dictionary<string, string>(Load(), StringComparer.OrdinalIgnoreCase);
        var clean = ModCategories.Normalize(path);

        if (clean.Length == 0)
            all.Remove(moduleId);
        else
            all[moduleId] = clean;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            var temporary = FilePath + ".tmp";

            File.WriteAllText(temporary, JsonSerializer.Serialize(all, Format));
            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
