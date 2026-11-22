using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// What switching from one enabled set to another does, by module id and ignoring order: the modules
// the switch turns on, the ones it turns off, and how many it leaves alone. Order is the load order
// comparer's business; this answers only "which mods are on".
public sealed record EnabledSetDiff(
    IReadOnlyList<string> TurnedOn,
    IReadOnlyList<string> TurnedOff,
    int Unchanged)
{
    public bool IsIdentical => TurnedOn.Count == 0 && TurnedOff.Count == 0;

    public static EnabledSetDiff Compute(IEnumerable<string> from, IEnumerable<string> to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var before = new HashSet<string>(from, StringComparer.OrdinalIgnoreCase);
        var after = new HashSet<string>(to, StringComparer.OrdinalIgnoreCase);

        return new(
            [.. after.Where(id => !before.Contains(id)).Order(StringComparer.OrdinalIgnoreCase)],
            [.. before.Where(id => !after.Contains(id)).Order(StringComparer.OrdinalIgnoreCase)],
            before.Count(after.Contains));
    }

    public static EnabledSetDiff Compute(LoadOrderSnapshot from, LoadOrderSnapshot to) =>
        Compute(EnabledIds(from), EnabledIds(to));

    public static IEnumerable<string> EnabledIds(LoadOrderSnapshot snapshot) =>
        snapshot.Entries.Where(entry => entry.IsEnabled).Select(entry => entry.Id);
}

// A switch to a named set: the order is kept and only enabled flags move, so no file is touched. Undo
// is the snapshot as it was, which puts the whole group back in one step.
public sealed record EnabledSetApplication(LoadOrderSnapshot Result, LoadOrderSnapshot Undo, EnabledSetDiff Diff);

public static class EnabledSetSwitch
{
    // Modules the current list holds and the set never names are turned off, because the set says
    // which mods are on. Ids the set names that the list does not hold are reported as missing rather
    // than invented as rows.
    public static EnabledSetApplication Apply(LoadOrderSnapshot current, IEnumerable<string> set, out IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(set);

        var wanted = new HashSet<string>(set, StringComparer.OrdinalIgnoreCase);
        var held = new HashSet<string>(current.Entries.Select(entry => entry.Id), StringComparer.OrdinalIgnoreCase);

        missing = [.. wanted.Where(id => !held.Contains(id)).Order(StringComparer.OrdinalIgnoreCase)];

        var result = current with
        {
            Entries = [.. current.Entries.Select(entry => entry with { IsEnabled = wanted.Contains(entry.Id) })]
        };

        return new EnabledSetApplication(result, current, EnabledSetDiff.Compute(current, result));
    }
}

public sealed record NamedEnabledSet(string Name, DateTime SavedUtc, IReadOnlyList<string> Enabled);

// Named enabled sets for one game version: "Vanilla plus QoL", "Everything". Saving over a name
// replaces it; the file is per version beside the profiles because a set names that version's modules.
public sealed class EnabledSetStore(string? filePath)
{
    public const string FileName = "enabled-sets.json";

    public const int MaxNameLength = 80;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string? FilePath { get; } = filePath;

    public static string DefaultPath(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, FileName);

    public IReadOnlyList<NamedEnabledSet> List()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
            return [];

        try
        {
            var stored = JsonSerializer.Deserialize<List<NamedEnabledSet>>(File.ReadAllText(FilePath)) ?? [];

            return [.. stored.Where(set => !string.IsNullOrWhiteSpace(set.Name)).OrderBy(set => set.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public NamedEnabledSet? Find(string name) =>
        List().FirstOrDefault(set => string.Equals(set.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool Save(string name, IEnumerable<string> enabled)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(FilePath))
            return false;

        var trimmed = name.Trim();

        if (trimmed.Length > MaxNameLength)
            trimmed = trimmed[..MaxNameLength].TrimEnd();

        var ids = enabled.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var all = List().Where(set => !string.Equals(set.Name, trimmed, StringComparison.OrdinalIgnoreCase)).ToList();

        all.Add(new NamedEnabledSet(trimmed, DateTime.UtcNow, ids));

        return Write(all);
    }

    public bool Delete(string name)
    {
        var all = List().ToList();

        return all.RemoveAll(set => string.Equals(set.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) > 0 && Write(all);
    }

    private bool Write(List<NamedEnabledSet> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath!)!);

            var temporary = FilePath + ".tmp";

            File.WriteAllText(temporary, JsonSerializer.Serialize(all, Format));
            File.Move(temporary, FilePath!, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
