using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Io;

namespace BannerlordEnvironmentManager.Core.Settings;

public enum ArchiveReason
{
    Orphan,
    ReplacedByImport,
    ReplacedByRestore
}

public enum RestoreOutcome
{
    Restored,
    RestoredOverExisting,
    NotFound,
    MissingArchive
}

public enum DiscardOutcome
{
    Discarded,
    IndexEntryOnly,
    NotFound
}

public sealed record ArchivedSettings(
    string Id,
    string Name,
    string RelativePath,
    string OriginPath,
    string StoredPath,
    long SizeBytes,
    int FileCount,
    DateTimeOffset ArchivedUtc,
    ArchiveReason Reason = ArchiveReason.Orphan);

public sealed class ModSettingsArchive
{
    public const string IndexFileName = "index.json";

    private const int MaxCollisionSuffix = 999;

    private static readonly JsonSerializerOptions IndexFormat = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ModSettingsArchive(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = rootPath;
    }

    // Per version. Each entry is a settings folder taken out of one version's user data, so a single
    // machine-wide index listed a v1.4.8 mod's settings on screen while v1.5.2 was selected. Restoring
    // one of those was never unsafe - every entry carries the absolute OriginPath and StoredPath it was
    // written with, and Restore moves between exactly those two - but the list said the wrong thing.
    // The resting version keeps the path it has always used, and because the paths inside the index are
    // absolute, an archive taken before this change is still listed and still restorable from it.
    public static string GetDefaultRoot(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, "SettingsArchive");

    public string RootPath { get; }

    public string IndexPath => Path.Combine(RootPath, IndexFileName);

    public IReadOnlyList<ArchivedSettings> Read()
    {
        if (!File.Exists(IndexPath))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<ArchivedSettings>>(File.ReadAllText(IndexPath), IndexFormat) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public ArchivedSettings Archive(ModSettingsFolder folder, ArchiveReason reason = ArchiveReason.Orphan)
    {
        var entry = Place(folder, reason);

        MoveDirectory(folder.FullPath, entry.StoredPath);
        Write([.. Read(), entry]);

        return entry;
    }

    public ArchivedSettings Backup(ModSettingsFolder folder, ArchiveReason reason = ArchiveReason.ReplacedByImport)
    {
        var entry = Place(folder, reason);

        DirectoryMover.Copy(folder.FullPath, entry.StoredPath);
        Write([.. Read(), entry]);

        return entry;
    }

    // A folder the mod has rewritten since archiving is not thrown away to make room: it is copied into
    // the archive first, so the restore itself stays reversible.
    public RestoreOutcome Restore(string id)
    {
        var entries = Read();
        var entry = entries.FirstOrDefault(item => item.Id == id);

        if (entry is null)
            return RestoreOutcome.NotFound;

        if (!Directory.Exists(entry.StoredPath))
            return RestoreOutcome.MissingArchive;

        var replaced = Directory.Exists(entry.OriginPath);
        var remaining = entries.Where(item => item.Id != id).ToList();

        if (replaced)
        {
            var (sizeBytes, fileCount) = ModSettingsScanner.Measure(entry.OriginPath);

            var superseded = Place(
                new ModSettingsFolder(entry.Name, entry.RelativePath, entry.OriginPath, sizeBytes, fileCount, DateTimeOffset.UtcNow),
                ArchiveReason.ReplacedByRestore);

            MoveDirectory(entry.OriginPath, superseded.StoredPath);
            remaining.Add(superseded);
        }

        MoveDirectory(entry.StoredPath, entry.OriginPath);
        Write(remaining);

        return replaced ? RestoreOutcome.RestoredOverExisting : RestoreOutcome.Restored;
    }

    // Removing the stored copy is the caller's to do so Core stays free of Windows: the app hands in
    // the Recycle Bin call. The index is rewritten only after the removal returned, so a removal that
    // threw leaves the entry listed rather than hiding a folder that is still on disk.
    public DiscardOutcome Discard(string id, Action<string> removeDirectory)
    {
        ArgumentNullException.ThrowIfNull(removeDirectory);

        var entries = Read();
        var entry = entries.FirstOrDefault(item => item.Id == id);

        if (entry is null)
            return DiscardOutcome.NotFound;

        var stored = Directory.Exists(entry.StoredPath);

        if (stored)
            removeDirectory(entry.StoredPath);

        Write([.. entries.Where(item => item.Id != id)]);

        return stored ? DiscardOutcome.Discarded : DiscardOutcome.IndexEntryOnly;
    }

    private ArchivedSettings Place(ModSettingsFolder folder, ArchiveReason reason)
    {
        ArgumentNullException.ThrowIfNull(folder);

        if (!Directory.Exists(folder.FullPath))
            throw new DirectoryNotFoundException($"'{folder.FullPath}' does not exist.");

        var archivedUtc = DateTimeOffset.UtcNow;
        var destination = FreePath(Path.Combine(
            RootPath, archivedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), SafeSegment(folder.Name)));

        return new ArchivedSettings(
            Guid.NewGuid().ToString("N"),
            folder.Name,
            folder.RelativePath,
            Path.GetFullPath(folder.FullPath),
            destination,
            folder.SizeBytes,
            folder.FileCount,
            archivedUtc,
            reason);
    }

    private void Write(IReadOnlyList<ArchivedSettings> entries)
    {
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(entries, IndexFormat));
    }

    private static void MoveDirectory(string source, string destination) =>
        DirectoryMover.Move(source, destination);

    private static string FreePath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
            return path;

        for (var suffix = 2; suffix <= MaxCollisionSuffix; suffix++)
        {
            var candidate = $"{path} ({suffix})";

            if (!Directory.Exists(candidate) && !File.Exists(candidate))
                return candidate;
        }

        throw new IOException($"'{path}' already holds {MaxCollisionSuffix} archived copies.");
    }

    private static string SafeSegment(string value)
    {
        var name = Path.GetFileName(value.Trim().TrimEnd('/', '\\'));

        return string.IsNullOrWhiteSpace(name) || name is "." or ".." ? "unknown" : name;
    }
}
