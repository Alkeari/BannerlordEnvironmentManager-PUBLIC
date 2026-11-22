using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Modules;

// What the player wrote about one mod: free text and short tags. Filed per game version beside the
// pins and the dividers, since a note such as "breaks the siege patch" describes one version's list.
public sealed record ModNote(string Text, IReadOnlyList<string> Tags, string Source = "")
{
    public bool IsEmpty => Text.Length == 0 && Tags.Count == 0 && Source.Length == 0;
}

public sealed class ModNotesStore(string? filePath)
{
    public const string FileName = "mod-notes.json";

    public const int MaxTextLength = 2000;

    public const int MaxTagLength = 32;

    public const int MaxTags = 12;

    public const int MaxSourceLength = 200;

    public static string CleanSource(string? source)
    {
        var clean = (source ?? string.Empty).Trim();

        return clean.Length > MaxSourceLength ? clean[..MaxSourceLength].TrimEnd() : clean;
    }

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string? FilePath { get; } = filePath;

    public static string DefaultPath(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, FileName);

    // "siege, QoL;  siege" becomes [siege, QoL]: split on comma, semicolon or newline, trimmed,
    // capped, and de-duplicated ignoring case while keeping the first spelling.
    public static IReadOnlyList<string> ParseTags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var tags = new List<string>();

        foreach (var raw in text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var tag = raw.Trim();

            if (tag.Length > MaxTagLength)
                tag = tag[..MaxTagLength].TrimEnd();

            if (tag.Length > 0 && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase) && tags.Count < MaxTags)
                tags.Add(tag);
        }

        return tags;
    }

    public static string JoinTags(IReadOnlyList<string> tags) => string.Join(", ", tags);

    public IReadOnlyDictionary<string, ModNote> Load()
    {
        var notes = new Dictionary<string, ModNote>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
            return notes;

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, StoredNote>>(File.ReadAllText(FilePath)) ?? [];

            foreach (var (id, note) in stored)
            {
                var value = new ModNote(note.Text ?? string.Empty, ParseTags(string.Join(',', note.Tags ?? [])), CleanSource(note.Source));

                if (!value.IsEmpty)
                    notes[id] = value;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return notes;
        }

        return notes;
    }

    public ModNote Get(string moduleId) =>
        Load().TryGetValue(moduleId, out var note) ? note : new ModNote(string.Empty, []);

    // An empty note and no tags removes the entry, so a cleared box leaves nothing behind.
    // A null source keeps the one already recorded; an empty one clears it.
    public bool Set(string moduleId, string? text, IReadOnlyList<string>? tags, string? source = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);

        if (string.IsNullOrWhiteSpace(FilePath))
            return false;

        var clean = (text ?? string.Empty).Trim();

        if (clean.Length > MaxTextLength)
            clean = clean[..MaxTextLength];

        var all = new Dictionary<string, ModNote>(Load(), StringComparer.OrdinalIgnoreCase);
        var kept = source is null && all.TryGetValue(moduleId, out var old) ? old.Source : CleanSource(source);
        var note = new ModNote(clean, ParseTags(string.Join(',', tags ?? [])), kept);

        if (note.IsEmpty)
            all.Remove(moduleId);
        else
            all[moduleId] = note;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            var stored = all
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(pair => pair.Key, pair => new StoredNote(pair.Value.Text, [.. pair.Value.Tags], pair.Value.Source));
            var temporary = FilePath + ".tmp";

            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, Format));
            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static IReadOnlyList<string> AllTags(IReadOnlyDictionary<string, ModNote> notes) =>
        [.. notes.Values.SelectMany(note => note.Tags).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];

    // Null or empty tag means no filter. A module with no note never matches a tag.
    public static bool Matches(IReadOnlyDictionary<string, ModNote> notes, string moduleId, string? tag) =>
        string.IsNullOrWhiteSpace(tag)
        || (notes.TryGetValue(moduleId, out var note) && note.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));

    private sealed record StoredNote(string? Text, List<string>? Tags, string? Source = null);
}
