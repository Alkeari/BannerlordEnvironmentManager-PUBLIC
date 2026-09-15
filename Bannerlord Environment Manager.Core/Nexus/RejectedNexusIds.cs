using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

// A mod page the user has already looked at and said is not this module. Kept because an index match
// is a resemblance rather than a proof, and a wrong one is not wrong once: without this the same page
// is proposed again on every search, and somebody who has already corrected it by hand has to correct
// it again. Rejecting is the other half of confirming, and only one of the two existed.
//
// A rejection is about the pair, not the module. Saying "this module is not mod 1234" leaves BEM free
// to propose mod 5678 for it, which is the whole point of looking again.
public sealed record RejectedNexusId(string ModuleId, int NexusModId, DateTimeOffset RecordedUtc);

public sealed record RejectionRecord(int Added, int Unchanged, int Removed, int Failed = 0)
{
    public string Describe() => this switch
    {
        { Failed: > 0 } => Strings.Current.Plural("Core.Nexus.RejectedIds.Describe.Failed", Failed),
        { Removed: > 0 } => Strings.Current.Plural("Core.Nexus.RejectedIds.Describe.Removed", Removed),
        { Added: > 0 } => Strings.Current.Plural("Core.Nexus.RejectedIds.Describe.Added", Added),
        { Unchanged: > 0 } => Strings.Current["Core.Nexus.RejectedIds.Describe.Unchanged"],
        _ => Strings.Current["Core.Nexus.RejectedIds.Describe.NothingWritten"]
    };
}

public sealed class RejectedNexusIdStore
{
    public const string FileName = "rejected-nexus-ids.json";

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public RejectedNexusIdStore(string? filePath) => FilePath = filePath;

    public static RejectedNexusIdStore Default { get; } = new(DefaultPath());

    public static RejectedNexusIdStore None { get; } = new((string?)null);

    public string? FilePath { get; }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        FileName);

    public IReadOnlyList<RejectedNexusId> Load()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            return [];

        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<RejectedNexusId>>(File.ReadAllText(FilePath), Format) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    public bool IsRejected(string moduleId, int nexusModId) =>
        Load().Any(rejection =>
            rejection.NexusModId == nexusModId
            && string.Equals(rejection.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));

    public RejectionRecord Record(IEnumerable<RejectedNexusId> rejections)
    {
        ArgumentNullException.ThrowIfNull(rejections);

        var incoming = rejections.Where(item => !string.IsNullOrWhiteSpace(item.ModuleId)).ToList();

        if (string.IsNullOrWhiteSpace(FilePath) || incoming.Count == 0)
            return new RejectionRecord(0, 0, 0);

        var existing = Load().ToList();
        var added = 0;
        var unchanged = 0;

        foreach (var rejection in incoming)
        {
            if (existing.Any(already =>
                    already.NexusModId == rejection.NexusModId
                    && string.Equals(already.ModuleId, rejection.ModuleId, StringComparison.OrdinalIgnoreCase)))
            {
                unchanged++;
                continue;
            }

            existing.Add(rejection);
            added++;
        }

        if (added > 0 && !Write(Ordered(existing)))
            return new RejectionRecord(0, 0, 0, added);

        return new RejectionRecord(added, unchanged, 0);
    }

    // A rejection the user can set and cannot take back would make a module they mis-clicked once
    // permanently unmatchable to the page it really came from.
    public RejectionRecord Forget(IEnumerable<RejectedNexusId> rejections)
    {
        ArgumentNullException.ThrowIfNull(rejections);

        var drop = rejections.ToList();

        if (string.IsNullOrWhiteSpace(FilePath) || drop.Count == 0)
            return new RejectionRecord(0, 0, 0);

        var all = Load();

        var kept = all
            .Where(item => !drop.Any(target =>
                target.NexusModId == item.NexusModId
                && string.Equals(target.ModuleId, item.ModuleId, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var removed = all.Count - kept.Count;

        if (removed > 0 && !Write(Ordered(kept)))
            return new RejectionRecord(0, 0, 0, removed);

        return new RejectionRecord(0, 0, removed);
    }

    private static List<RejectedNexusId> Ordered(IEnumerable<RejectedNexusId> rejections) =>
    [
        .. rejections
            .OrderBy(item => item.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.NexusModId)
    ];

    private bool Write(IReadOnlyList<RejectedNexusId> rejections)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath!)!);

            var pending = FilePath + ".tmp";

            File.WriteAllText(pending, JsonSerializer.Serialize(rejections, Format));
            File.Move(pending, FilePath!, overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
