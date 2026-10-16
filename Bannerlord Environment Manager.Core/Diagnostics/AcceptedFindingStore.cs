using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// The Health/Diagnostics counterpart to Launcher's AcceptedRiskStore, for everything that is not a
// preflight finding: an install check, a mod safety alarm, a boot check result. Kept as its own store
// rather than reusing the preflight one because those findings are keyed by PreflightCheck, a closed
// enum that means nothing to a rig conflict or a flagged file. Category here is deliberately a plain
// string instead: each family names its own (RigIssue, AssemblyIssue, ModSafety, BootCheck, ...), and
// nothing here needs to know the full list of them.
//
// Keyed on (Category, Headline), never on a module id or file path alone, for the same reason as the
// preflight store: a module that trades one problem for a different one must not inherit an acceptance
// that was only ever about the first. A changed headline is a changed key, and the safe failure is the
// finding coming back rather than staying hidden.
public sealed record AcceptedFinding(string Category, string Headline, DateTimeOffset RecordedUtc)
{
    public string Key => AcceptedFindingStore.KeyFor(Category, Headline);
}

public sealed record AcceptFindingRecord(int Added, int Unchanged, int Removed, int Failed = 0)
{
    public string Describe() => this switch
    {
        { Failed: > 0 } => Strings.Current.Plural("Core.Diagnostics.AcceptedFinding.Failed", Failed),
        { Removed: > 0 } => Strings.Current.Plural("Core.Diagnostics.AcceptedFinding.Removed", Removed),
        { Added: > 0 } => Strings.Current.Plural("Core.Diagnostics.AcceptedFinding.Added", Added),
        { Unchanged: > 0 } => Strings.Current["Core.Diagnostics.AcceptedFinding.Unchanged"],
        _ => Strings.Current["Core.Diagnostics.AcceptedFinding.NothingWritten"]
    };
}

public sealed class AcceptedFindingStore
{
    public const string FileName = "accepted-findings.json";

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AcceptedFindingStore(string? filePath) => FilePath = filePath;

    public static AcceptedFindingStore Default { get; } = new(DefaultPath());

    public static AcceptedFindingStore None { get; } = new((string?)null);

    public string? FilePath { get; }

    // Per version: this file describes one specific load order, so a machine-wide copy showed one
    // version's answer while another was selected. The resting version keeps the path it has always
    // used; every other version reads and writes its own.
    public static string DefaultPath(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, FileName);

    public static string KeyFor(string category, string headline) => $"{category}|{headline}";

    public IReadOnlyList<AcceptedFinding> Load()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            return [];

        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<AcceptedFinding>>(File.ReadAllText(FilePath), Format) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    // What every finding-building pass actually consumes: the set of keys, not the records. Loaded
    // fresh every time rather than cached, so accepting or forgetting from one page is seen by the
    // next recheck without either page having to know the other exists.
    public IReadOnlySet<string> LoadKeys() =>
        Load().Select(finding => finding.Key).ToHashSet(StringComparer.Ordinal);

    public AcceptFindingRecord Accept(IEnumerable<AcceptedFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var incoming = findings.Where(finding => !string.IsNullOrWhiteSpace(finding.Headline)).ToList();

        if (string.IsNullOrWhiteSpace(FilePath) || incoming.Count == 0)
            return new AcceptFindingRecord(0, 0, 0);

        var existing = Load().ToList();
        var added = 0;
        var unchanged = 0;

        foreach (var finding in incoming)
        {
            if (existing.Any(already => already.Key == finding.Key))
            {
                unchanged++;
                continue;
            }

            existing.Add(finding);
            added++;
        }

        if (added > 0 && !Write(Ordered(existing)))
            return new AcceptFindingRecord(0, 0, 0, added);

        return new AcceptFindingRecord(added, unchanged, 0);
    }

    public AcceptFindingRecord Forget(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var drop = keys.ToHashSet(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(FilePath) || drop.Count == 0)
            return new AcceptFindingRecord(0, 0, 0);

        var all = Load();
        var kept = all.Where(finding => !drop.Contains(finding.Key)).ToList();
        var removed = all.Count - kept.Count;

        if (removed > 0 && !Write(Ordered(kept)))
            return new AcceptFindingRecord(0, 0, 0, removed);

        return new AcceptFindingRecord(0, 0, removed);
    }

    private static List<AcceptedFinding> Ordered(IEnumerable<AcceptedFinding> findings) =>
    [
        .. findings
            .OrderBy(finding => finding.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.Headline, StringComparer.OrdinalIgnoreCase)
    ];

    private bool Write(IReadOnlyList<AcceptedFinding> findings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath!)!);

            var pending = FilePath + ".tmp";

            File.WriteAllText(pending, JsonSerializer.Serialize(findings, Format));
            File.Move(pending, FilePath!, overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
