using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Io;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public sealed record SetAsideRecord(
    string Bundler,
    string BundlerFolder,
    DateTime CreatedUtc,
    IReadOnlyList<string> SubModuleClasses,
    IReadOnlyList<string> Files,
    string RewrittenManifestHash)
{
    public string BackupFolder { get; init; } = string.Empty;
}

public enum UndoOutcome
{
    Restored,

    // The mod's SubModule.xml is no longer the one BEM wrote, which means the mod was updated or
    // reinstalled since. Putting the old manifest and DLLs back over a newer release would downgrade
    // it, so nothing is touched and the backup is dropped as stale.
    ModChangedSince,

    BackupMissing
}

// Makes a bundling mod stop declaring and carrying what an enabled BUTR stack module already provides,
// and puts it back. Everything taken out is moved under BEM's state folder with the manifest as it
// was, so the change is one press to undo.
public sealed class BundledStackSetAsideStore
{
    private const string RecordFileName = "set-aside.json";

    private const string ManifestName = "SubModule.xml";

    private const string FilesFolderName = "files";

    private const string TimestampFormat = "yyyy-MM-dd HH-mm-ss-fff";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public BundledStackSetAsideStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = rootPath;
    }

    public string RootPath { get; }

    public static string GetDefaultRoot(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, "bundled-stack-set-aside");

    public SetAsideRecord Apply(BundledStackCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);

        var manifestPath = Path.Combine(copy.BundlerFolder, ManifestName);
        var created = DateTime.UtcNow;
        var backup = Reserve(Path.Combine(RootPath, Sanitize(copy.Bundler.Value)), created);
        Directory.CreateDirectory(backup);

        File.Copy(manifestPath, Path.Combine(backup, ManifestName));

        var document = XDocument.Load(manifestPath, LoadOptions.PreserveWhitespace);
        var classes = copy.SubModuleClasses.ToHashSet(StringComparer.Ordinal);

        foreach (var subModule in document.Root?.Element("SubModules")?.Elements("SubModule").ToList() ?? [])
        {
            var className = subModule.Element("SubModuleClassType")?.Attribute("value")?.Value.Trim();

            if (className is not null && classes.Contains(className))
                subModule.Remove();
        }

        var moved = new List<string>();

        foreach (var relative in copy.Files)
        {
            var source = Path.Combine(copy.BundlerFolder, relative);

            if (!File.Exists(source))
                continue;

            var target = Path.Combine(backup, FilesFolderName, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target);
            moved.Add(relative);
        }

        AtomicXmlFile.Save(document, manifestPath, writeBackup: false);

        var record = new SetAsideRecord(
            copy.Bundler.Value,
            copy.BundlerFolder,
            created,
            copy.SubModuleClasses,
            moved,
            HashOf(manifestPath));

        File.WriteAllText(Path.Combine(backup, RecordFileName), JsonSerializer.Serialize(record, Format));

        return record with { BackupFolder = backup };
    }

    public IReadOnlyList<SetAsideRecord> List()
    {
        if (!Directory.Exists(RootPath))
            return [];

        var records = new List<SetAsideRecord>();

        foreach (var path in Directory.EnumerateFiles(RootPath, RecordFileName, SearchOption.AllDirectories))
        {
            try
            {
                if (JsonSerializer.Deserialize<SetAsideRecord>(File.ReadAllText(path), Format) is { } record)
                    records.Add(record with { BackupFolder = Path.GetDirectoryName(path)! });
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _ = ex;
            }
        }

        return [.. records.OrderByDescending(record => record.CreatedUtc)];
    }

    public UndoOutcome Undo(SetAsideRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var savedManifest = Path.Combine(record.BackupFolder, ManifestName);

        if (!File.Exists(savedManifest))
            return UndoOutcome.BackupMissing;

        var manifestPath = Path.Combine(record.BundlerFolder, ManifestName);

        if (!File.Exists(manifestPath) || HashOf(manifestPath) != record.RewrittenManifestHash)
        {
            Directory.Delete(record.BackupFolder, recursive: true);
            RemoveIfEmpty(Path.GetDirectoryName(record.BackupFolder)!);
            return UndoOutcome.ModChangedSince;
        }

        foreach (var relative in record.Files)
        {
            var source = Path.Combine(record.BackupFolder, FilesFolderName, relative);

            if (!File.Exists(source))
                continue;

            var target = Path.Combine(record.BundlerFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target, overwrite: true);
        }

        File.Copy(savedManifest, manifestPath, overwrite: true);
        Directory.Delete(record.BackupFolder, recursive: true);
        RemoveIfEmpty(Path.GetDirectoryName(record.BackupFolder)!);

        return UndoOutcome.Restored;
    }

    private void RemoveIfEmpty(string bundlerBackups)
    {
        if (Directory.Exists(bundlerBackups) && !Directory.EnumerateFileSystemEntries(bundlerBackups).Any())
            Directory.Delete(bundlerBackups);

        if (Directory.Exists(RootPath) && !Directory.EnumerateFileSystemEntries(RootPath).Any())
            Directory.Delete(RootPath);
    }

    private static string HashOf(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Reserve(string folder, DateTime stamp)
    {
        while (true)
        {
            var path = Path.Combine(folder, stamp.ToString(TimestampFormat, CultureInfo.InvariantCulture));

            if (!Directory.Exists(path))
                return path;

            stamp = stamp.AddMilliseconds(1);
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();

        return new string([.. name.Where(c => !invalid.Contains(c))]).Trim();
    }
}
