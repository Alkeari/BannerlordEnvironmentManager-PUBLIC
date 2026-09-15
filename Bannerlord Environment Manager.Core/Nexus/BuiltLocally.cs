using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

// The user's own statement that the copy of this module on disk was compiled here rather than
// downloaded. It is a different statement from "not published on Nexus": these modules do have a mod
// page, the page is worth opening, and the one thing nobody can do is compare a local build against a
// published release. A development build is routinely ahead of its own page, which BEM can only report
// as an unexplained disagreement between two version strings.
//
// Nothing here is ever inferred. A version higher than the page's looks exactly the same whether it is
// a local build, a hand-patched copy, or an author who numbers a page and its files differently, and
// guessing between those is how a wrong verdict gets presented as a finding.
public sealed record ModuleBuiltLocally(string ModuleId, string Reason, DateTimeOffset RecordedUtc);

public sealed record BuiltLocallyRecord(int Added, int Unchanged, int Removed, int Failed = 0)
{
    public string Describe(string moduleId) => this switch
    {
        { Failed: > 0 } => Strings.Current.Format("Core.Nexus.BuiltLocally.Describe.Failed", moduleId),
        { Removed: > 0 } => Strings.Current.Format("Core.Nexus.BuiltLocally.Describe.Removed", moduleId),
        { Added: > 0 } => Strings.Current.Format("Core.Nexus.BuiltLocally.Describe.Added", moduleId),
        { Unchanged: > 0 } => Strings.Current.Format("Core.Nexus.BuiltLocally.Describe.Unchanged", moduleId),
        _ => Strings.Current.Format("Core.Nexus.BuiltLocally.Describe.NothingWritten", moduleId)
    };
}

public sealed class BuiltLocallyStore
{
    public const string FileName = "built-locally.json";

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public BuiltLocallyStore(string? filePath) => FilePath = filePath;

    public static BuiltLocallyStore Default { get; } = new(DefaultPath());

    public static BuiltLocallyStore None { get; } = new((string?)null);

    public string? FilePath { get; }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        FileName);

    public IReadOnlyList<ModuleBuiltLocally> Load()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            return [];

        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<ModuleBuiltLocally>>(File.ReadAllText(FilePath), Format) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    public IReadOnlyDictionary<string, ModuleBuiltLocally> ByModuleId()
    {
        var marks = new Dictionary<string, ModuleBuiltLocally>(StringComparer.OrdinalIgnoreCase);

        foreach (var mark in Load().OrderBy(mark => mark.RecordedUtc))
            marks[mark.ModuleId] = mark;

        return marks;
    }

    public BuiltLocallyRecord Record(IEnumerable<ModuleBuiltLocally> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);

        var incoming = marks.Where(mark => !string.IsNullOrWhiteSpace(mark.ModuleId)).ToList();

        if (string.IsNullOrWhiteSpace(FilePath) || incoming.Count == 0)
            return new BuiltLocallyRecord(0, 0, 0);

        var existing = Load().ToDictionary(mark => mark.ModuleId, StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var unchanged = 0;

        foreach (var mark in incoming)
        {
            if (existing.TryGetValue(mark.ModuleId, out var already) && already.Reason == mark.Reason)
            {
                unchanged++;
                continue;
            }

            existing[mark.ModuleId] = mark;
            added++;
        }

        if (added > 0 && !Write([.. existing.Values.OrderBy(mark => mark.ModuleId, StringComparer.OrdinalIgnoreCase)]))
            return new BuiltLocallyRecord(0, 0, 0, added);

        return new BuiltLocallyRecord(added, unchanged, 0);
    }

    public BuiltLocallyRecord Forget(IEnumerable<string> moduleIds)
    {
        ArgumentNullException.ThrowIfNull(moduleIds);

        var drop = moduleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(FilePath) || drop.Count == 0)
            return new BuiltLocallyRecord(0, 0, 0);

        var all = Load();
        var kept = all.Where(mark => !drop.Contains(mark.ModuleId)).ToList();
        var removed = all.Count - kept.Count;

        if (removed > 0 && !Write([.. kept.OrderBy(mark => mark.ModuleId, StringComparer.OrdinalIgnoreCase)]))
            return new BuiltLocallyRecord(0, 0, 0, removed);

        return new BuiltLocallyRecord(0, 0, removed);
    }

    private bool Write(IReadOnlyList<ModuleBuiltLocally> marks)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath!)!);

            var pending = FilePath + ".tmp";

            File.WriteAllText(pending, JsonSerializer.Serialize(marks, Format));
            File.Move(pending, FilePath!, overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
