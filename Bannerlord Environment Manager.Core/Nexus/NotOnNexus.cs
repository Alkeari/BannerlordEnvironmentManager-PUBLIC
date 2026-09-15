using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

// The user's own statement that a module is not published on Nexus at all. BEM cannot work this out:
// "no install record and no mod id" is equally consistent with a mod somebody wrote themselves and one
// they extracted by hand from an archive BEM never saw, and guessing between those is how a real gap
// gets hidden. So nothing here is ever inferred, and it lives in its own file rather than beside the
// ids BEM learned, so a statement the user made stays tellable from a conclusion BEM reached.
//
// Reason is the user's own words, kept so the mark can be argued with later rather than merely
// trusted. It may be empty: making somebody justify themselves before they can silence a false worry
// is a tax, not a safeguard.
public sealed record ModuleNotOnNexus(string ModuleId, string Reason, DateTimeOffset RecordedUtc);

// Failed is the count the file would have held if the write had worked. A write that could not happen
// used to report the same numbers as one that did, which made "you marked it" a sentence BEM printed
// over a disk that had not changed.
public sealed record NotOnNexusRecord(int Added, int Unchanged, int Removed, int Failed = 0)
{
    // "Marked it", "you already said so" and "nothing was written" are three different statements, and
    // picking the wrong one is as much a defect as a wrong result. Removed is read first because Forget
    // reports through this same record and reports nothing else.
    public string Describe(string moduleId) => this switch
    {
        { Failed: > 0 } => Strings.Current.Format("Core.Nexus.NotOnNexus.Describe.Failed", moduleId),
        { Removed: > 0 } => Strings.Current.Format("Core.Nexus.NotOnNexus.Describe.Removed", moduleId),
        { Added: > 0 } => Strings.Current.Format("Core.Nexus.NotOnNexus.Describe.Added", moduleId),
        { Unchanged: > 0 } => Strings.Current.Format("Core.Nexus.NotOnNexus.Describe.Unchanged", moduleId),
        _ => Strings.Current.Format("Core.Nexus.NotOnNexus.Describe.NothingWritten", moduleId)
    };
}

public sealed class NotOnNexusStore
{
    public const string FileName = "not-on-nexus.json";

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public NotOnNexusStore(string? filePath) => FilePath = filePath;

    public static NotOnNexusStore Default { get; } = new(DefaultPath());

    // For a caller that wants nothing this machine happens to hold, and for tests.
    public static NotOnNexusStore None { get; } = new((string?)null);

    public string? FilePath { get; }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        FileName);

    public IReadOnlyList<ModuleNotOnNexus> Load()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            return [];

        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<ModuleNotOnNexus>>(File.ReadAllText(FilePath), Format) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    public IReadOnlyDictionary<string, ModuleNotOnNexus> ByModuleId()
    {
        var marks = new Dictionary<string, ModuleNotOnNexus>(StringComparer.OrdinalIgnoreCase);

        foreach (var mark in Load().OrderBy(mark => mark.RecordedUtc))
            marks[mark.ModuleId] = mark;

        return marks;
    }

    public NotOnNexusRecord Record(IEnumerable<ModuleNotOnNexus> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);

        var incoming = marks.Where(mark => !string.IsNullOrWhiteSpace(mark.ModuleId)).ToList();

        if (string.IsNullOrWhiteSpace(FilePath) || incoming.Count == 0)
            return new NotOnNexusRecord(0, 0, 0);

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
            return new NotOnNexusRecord(0, 0, 0, added);

        return new NotOnNexusRecord(added, unchanged, 0);
    }

    // The other direction. A mark the user can set and cannot take back would turn a convenience into
    // a module they can never check again.
    public NotOnNexusRecord Forget(IEnumerable<string> moduleIds)
    {
        ArgumentNullException.ThrowIfNull(moduleIds);

        var drop = moduleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(FilePath) || drop.Count == 0)
            return new NotOnNexusRecord(0, 0, 0);

        var all = Load();
        var kept = all.Where(mark => !drop.Contains(mark.ModuleId)).ToList();
        var removed = all.Count - kept.Count;

        if (removed > 0 && !Write([.. kept.OrderBy(mark => mark.ModuleId, StringComparer.OrdinalIgnoreCase)]))
            return new NotOnNexusRecord(0, 0, 0, removed);

        return new NotOnNexusRecord(0, 0, removed);
    }

    // Written beside the real file and moved over it, so a write that stops halfway leaves the marks
    // already recorded intact rather than truncated. False means nothing on disk changed, and the
    // caller has to say so: a write that failed silently is the interface claiming a mark BEM does not
    // hold, and the user would only find out when update checking asked about the module anyway.
    private bool Write(IReadOnlyList<ModuleNotOnNexus> marks)
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
