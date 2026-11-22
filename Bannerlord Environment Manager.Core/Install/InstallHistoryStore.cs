using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Install;

// How a module arrived or left, as Vortex's download list and MO2's installation history show it.
public enum InstallHistoryKind
{
    Installed,
    Updated,
    Removed
}

// One line of the history: which module, which version, from which archive and Nexus file, and when.
public sealed record InstallHistoryEntry(
    DateTime WhenUtc,
    InstallHistoryKind Kind,
    string ModuleId,
    string? Version,
    string? ArchivePath,
    int? NexusModId,
    int? NexusFileId);

// A persistent, capped history of installs, updates and removals for one game version. It also answers
// the rollback question: which earlier archive of a module is still on disk, newest first.
public sealed class InstallHistoryStore(string? filePath, int capacity = InstallHistoryStore.DefaultCapacity)
{
    public const string FileName = "install-history.json";

    public const int DefaultCapacity = 500;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string? FilePath { get; } = filePath;

    public static string DefaultPath(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, FileName);

    // Newest first.
    public IReadOnlyList<InstallHistoryEntry> Load()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
            return [];

        try
        {
            var stored = JsonSerializer.Deserialize<List<InstallHistoryEntry>>(File.ReadAllText(FilePath)) ?? [];

            return [.. stored.Where(entry => !string.IsNullOrWhiteSpace(entry.ModuleId)).OrderByDescending(entry => entry.WhenUtc)];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public bool Record(InstallHistoryEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.ModuleId);

        if (string.IsNullOrWhiteSpace(FilePath))
            return false;

        var all = Load().Prepend(entry).OrderByDescending(item => item.WhenUtc).Take(Math.Max(1, capacity)).ToList();

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

    // An arrival is an update when the module already has a live entry (installed or updated, not
    // since removed), otherwise a first install.
    public bool RecordArrival(
        string moduleId, string? version, string? archivePath, int? nexusModId, int? nexusFileId, DateTime? whenUtc = null)
    {
        var latest = For(moduleId).FirstOrDefault();
        var kind = latest is { Kind: not InstallHistoryKind.Removed } ? InstallHistoryKind.Updated : InstallHistoryKind.Installed;

        return Record(new InstallHistoryEntry(whenUtc ?? DateTime.UtcNow, kind, moduleId, version, archivePath, nexusModId, nexusFileId));
    }

    public bool RecordRemoval(string moduleId, string? version, DateTime? whenUtc = null) =>
        Record(new InstallHistoryEntry(whenUtc ?? DateTime.UtcNow, InstallHistoryKind.Removed, moduleId, version, null, null, null));

    public IReadOnlyList<InstallHistoryEntry> For(string moduleId) =>
        [.. Load().Where(entry => string.Equals(entry.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase))];

    // Earlier versions of a module whose archive is still on disk, newest first, one per version, the
    // version currently installed excluded. Each is a candidate to roll back to.
    public IReadOnlyList<InstallHistoryEntry> RollbackCandidates(string moduleId, string? installedVersion) =>
        [.. For(moduleId)
            .Where(entry => entry.Kind != InstallHistoryKind.Removed
                && !string.IsNullOrWhiteSpace(entry.Version)
                && !string.Equals(entry.Version, installedVersion, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.ArchivePath)
                && File.Exists(entry.ArchivePath))
            .DistinctBy(entry => entry.Version, StringComparer.OrdinalIgnoreCase)];
}
