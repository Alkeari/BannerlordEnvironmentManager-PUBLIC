using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Launcher;

// A preflight finding the user has already read and chosen to launch past. Keyed on the check and the
// exact headline rather than the module, because a module that trades one broken patch target for a
// different one later must not inherit an acceptance that was only ever about the first patch. A
// wording change is the safe failure: it re-surfaces a finding rather than silently keeping it hidden.
public sealed record AcceptedRisk(PreflightCheck Check, string Headline, DateTimeOffset RecordedUtc)
{
    public string Key => PreflightFinding.KeyFor(Check, Headline);
}

public sealed record AcceptRecord(int Added, int Unchanged, int Removed, int Failed = 0)
{
    public string Describe() => this switch
    {
        { Failed: > 0 } => Strings.Current.Plural("Core.Launcher.AcceptRecord.Failed", Failed),
        { Removed: > 0 } => Strings.Current.Plural("Core.Launcher.AcceptRecord.Removed", Removed),
        { Added: > 0 } => Strings.Current.Plural("Core.Launcher.AcceptRecord.Added", Added),
        { Unchanged: > 0 } => Strings.Current["Core.Launcher.AcceptRecord.Unchanged"],
        _ => Strings.Current["Core.Launcher.AcceptRecord.Nothing"]
    };
}

public sealed class AcceptedRiskStore
{
    public const string FileName = "accepted-preflight-risks.json";

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AcceptedRiskStore(string? filePath) => FilePath = filePath;

    public static AcceptedRiskStore Default { get; } = new(DefaultPath());

    public static AcceptedRiskStore None { get; } = new((string?)null);

    public string? FilePath { get; }

    // Per version: this file describes one specific load order, so a machine-wide copy showed one
    // version's answer while another was selected. The resting version keeps the path it has always
    // used; every other version reads and writes its own.
    public static string DefaultPath(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, FileName);

    public IReadOnlyList<AcceptedRisk> Load()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            return [];

        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<AcceptedRisk>>(File.ReadAllText(FilePath), Format) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    // What Inspect actually consumes: the set of keys, not the records. Loaded fresh every preflight
    // rather than cached, so accepting or forgetting a risk from one page is seen by the next launch
    // without either page having to know the other exists.
    public IReadOnlySet<string> LoadKeys() =>
        Load().Select(risk => risk.Key).ToHashSet(StringComparer.Ordinal);

    public AcceptRecord Accept(IEnumerable<AcceptedRisk> risks)
    {
        ArgumentNullException.ThrowIfNull(risks);

        var incoming = risks.Where(risk => !string.IsNullOrWhiteSpace(risk.Headline)).ToList();

        if (string.IsNullOrWhiteSpace(FilePath) || incoming.Count == 0)
            return new AcceptRecord(0, 0, 0);

        var existing = Load().ToList();
        var added = 0;
        var unchanged = 0;

        foreach (var risk in incoming)
        {
            if (existing.Any(already => already.Key == risk.Key))
            {
                unchanged++;
                continue;
            }

            existing.Add(risk);
            added++;
        }

        if (added > 0 && !Write(Ordered(existing)))
            return new AcceptRecord(0, 0, 0, added);

        return new AcceptRecord(added, unchanged, 0);
    }

    // The other half of Accept: a risk accepted for one build of a mod must not stay accepted forever
    // once the user wants to look at it again.
    public AcceptRecord Forget(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var drop = keys.ToHashSet(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(FilePath) || drop.Count == 0)
            return new AcceptRecord(0, 0, 0);

        var all = Load();
        var kept = all.Where(risk => !drop.Contains(risk.Key)).ToList();
        var removed = all.Count - kept.Count;

        if (removed > 0 && !Write(Ordered(kept)))
            return new AcceptRecord(0, 0, 0, removed);

        return new AcceptRecord(0, 0, removed);
    }

    private static List<AcceptedRisk> Ordered(IEnumerable<AcceptedRisk> risks) =>
    [
        .. risks
            .OrderBy(risk => risk.Check)
            .ThenBy(risk => risk.Headline, StringComparer.OrdinalIgnoreCase)
    ];

    private bool Write(IReadOnlyList<AcceptedRisk> risks)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath!)!);

            var pending = FilePath + ".tmp";

            File.WriteAllText(pending, JsonSerializer.Serialize(risks, Format));
            File.Move(pending, FilePath!, overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
