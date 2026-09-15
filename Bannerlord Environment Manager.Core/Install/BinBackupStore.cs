using System.Globalization;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Io;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Install;

public sealed record BinBackup(
    string PlatformFolder,
    string RelativePath,
    string BackupPath,
    string DestinationPath,
    DateTime CreatedUtc,
    string Label,
    long SizeBytes,
    bool IsLegacy)
{
    public string GameRelativePath =>
        Path.Combine("bin", PlatformFolder, RelativePath.Replace('/', Path.DirectorySeparatorChar));

    public string DisplayName
    {
        get
        {
            var parts = new List<string>
            {
                GameRelativePath,
                CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
                FormatSize(SizeBytes)
            };

            if (Label.Length > 0)
                parts.Add(Label);

            if (IsLegacy)
                parts.Add(Strings.Current["Core.Install.BinBackup.StillInGameFolder"]);

            return string.Join(" - ", parts);
        }
    }

    // A list item with no automation name of its own is announced by its ToString, and a record's own
    // ToString reads the type name and every member aloud.
    public override string ToString() => DisplayName;

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };
}

// A backup written into the game's own bin folder is one Steam file verification away from being
// deleted, and it is invisible to anything but a file browser. Backups live under BEM's own state
// folder instead, and every generation is kept: the oldest holds the file the game shipped, the
// newer ones hold whatever a later install or restore replaced.
public sealed class BinBackupStore
{
    public const string LegacyExtension = AtomicXmlFile.BackupExtension;

    private const string Extension = ".bak";

    private const string TimestampFormat = "yyyy-MM-dd HH-mm-ss-fff";

    private static string MigratedLabel => Strings.Current["Core.Install.BinBackup.MigratedLabel"];

    public BinBackupStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = rootPath;
    }

    public string RootPath { get; }

    // Per version. One machine-wide store keyed on nothing but the platform folder and the relative
    // path listed a backup of one version's original DLL as belonging to whichever version was
    // selected, because Describe builds every DestinationPath from the caller's own game folder, and
    // Restore all originals then copied that version's binary over another version's with overwrite.
    // The resting version keeps the exact folder it has always used, so every backup already taken
    // stays where it is and stays restorable.
    public static string GetDefaultRoot(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, "bin-backups");

    public BinBackup? Capture(string gameInstallPath, string platformFolder, string relativePath, string label)
    {
        MigrateFile(gameInstallPath, platformFolder, relativePath);

        var destination = BinPayloadInstaller.DestinationPathFor(gameInstallPath, platformFolder, relativePath);

        if (!File.Exists(destination))
            return null;

        var newest = Generations(gameInstallPath, platformFolder, relativePath).FirstOrDefault();

        if (newest is not null && SameContent(newest.BackupPath, destination))
            return newest;

        var folder = FolderFor(platformFolder, relativePath);
        Directory.CreateDirectory(folder);

        var path = Reserve(folder, label, DateTime.UtcNow);
        File.Copy(destination, path, overwrite: false);

        return Describe(gameInstallPath, platformFolder, relativePath, path);
    }

    public IReadOnlyList<BinBackup> List(string gameInstallPath)
    {
        var backups = new List<BinBackup>();

        if (Directory.Exists(RootPath))
        {
            try
            {
                foreach (var platformRoot in Directory.EnumerateDirectories(RootPath))
                {
                    var platformFolder = Path.GetFileName(platformRoot);

                    foreach (var path in Directory.EnumerateFiles(platformRoot, "*" + Extension, SearchOption.AllDirectories))
                    {
                        var relativePath = Path.GetRelativePath(platformRoot, Path.GetDirectoryName(path)!);
                        backups.Add(Describe(gameInstallPath, platformFolder, relativePath, path));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A store that cannot be read must not hide the backups still sitting in the game folder.
            }
        }

        backups.AddRange(FindLegacy(gameInstallPath));

        return [.. backups
            .OrderBy(backup => backup.GameRelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(backup => backup.CreatedUtc)];
    }

    // Restoring everything means putting each file back to the copy the game shipped, which is the
    // oldest generation. Replaying a newer one would reinstate a modded file instead.
    public IReadOnlyList<BinBackup> Originals(string gameInstallPath) =>
        [.. List(gameInstallPath)
            .GroupBy(backup => backup.GameRelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(backup => backup.CreatedUtc).First())
            .OrderBy(backup => backup.GameRelativePath, StringComparer.OrdinalIgnoreCase)];

    public int Migrate(string gameInstallPath)
    {
        var migrated = 0;

        foreach (var legacy in FindLegacy(gameInstallPath))
        {
            try
            {
                if (MigrateFile(gameInstallPath, legacy.PlatformFolder, legacy.RelativePath) is not null)
                    migrated++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The backup stays where it is and stays restorable; the next migration retries it.
            }
        }

        return migrated;
    }

    // What is being replaced is captured first, so restoring the wrong generation is itself undoable.
    public bool Restore(string gameInstallPath, BinBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);

        var source = backup.IsLegacy
            ? MigrateFile(gameInstallPath, backup.PlatformFolder, backup.RelativePath)
            : backup;

        if (source is null || !File.Exists(source.BackupPath))
            return false;

        Capture(gameInstallPath, backup.PlatformFolder, backup.RelativePath,
            Strings.Current["Core.Install.BinBackup.BeforeRestoreLabel"]);

        var destination = BinPayloadInstaller.DestinationPathFor(gameInstallPath, backup.PlatformFolder, backup.RelativePath);
        var directory = Path.GetDirectoryName(destination);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.Copy(source.BackupPath, destination, overwrite: true);

        return true;
    }

    public BackupRestoreResult RestoreAll(string gameInstallPath, IEnumerable<BinBackup> backups)
    {
        ArgumentNullException.ThrowIfNull(backups);

        var restored = 0;
        var missing = 0;
        var failed = new List<string>();

        foreach (var backup in backups)
        {
            try
            {
                if (Restore(gameInstallPath, backup))
                    restored++;
                else
                    missing++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(backup.GameRelativePath);
            }
        }

        return new BackupRestoreResult(restored, missing, failed);
    }

    private BinBackup? MigrateFile(string gameInstallPath, string platformFolder, string relativePath)
    {
        var legacyPath = BinPayloadInstaller.DestinationPathFor(gameInstallPath, platformFolder, relativePath) + LegacyExtension;

        if (!File.Exists(legacyPath))
            return null;

        var folder = FolderFor(platformFolder, relativePath);
        Directory.CreateDirectory(folder);

        var path = Reserve(folder, MigratedLabel, File.GetLastWriteTimeUtc(legacyPath));
        File.Move(legacyPath, path);

        return Describe(gameInstallPath, platformFolder, relativePath, path);
    }

    private IReadOnlyList<BinBackup> Generations(string gameInstallPath, string platformFolder, string relativePath)
    {
        var folder = FolderFor(platformFolder, relativePath);

        if (!Directory.Exists(folder))
            return [];

        return [.. Directory.EnumerateFiles(folder, "*" + Extension)
            .Select(path => Describe(gameInstallPath, platformFolder, relativePath, path))
            .OrderByDescending(backup => backup.CreatedUtc)
            .ThenByDescending(backup => backup.BackupPath, StringComparer.OrdinalIgnoreCase)];
    }

    private static List<BinBackup> FindLegacy(string gameInstallPath)
    {
        var found = new List<BinBackup>();
        var binRoot = Path.Combine(gameInstallPath, "bin");

        if (!Directory.Exists(binRoot))
            return found;

        try
        {
            foreach (var platformRoot in Directory.EnumerateDirectories(binRoot))
            {
                var platformFolder = Path.GetFileName(platformRoot);

                foreach (var path in Directory.EnumerateFiles(platformRoot, "*" + LegacyExtension, SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(platformRoot, path[..^LegacyExtension.Length]);

                    found.Add(new BinBackup(
                        platformFolder,
                        Normalize(relativePath),
                        path,
                        path[..^LegacyExtension.Length],
                        File.GetLastWriteTimeUtc(path),
                        string.Empty,
                        SizeOf(path),
                        IsLegacy: true));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable bin folder is reported as no legacy backups rather than as a crash.
        }

        return found;
    }

    private static BinBackup Describe(string gameInstallPath, string platformFolder, string relativePath, string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);

        var created = name.Length >= TimestampFormat.Length
            && DateTime.TryParseExact(
                name[..TimestampFormat.Length], TimestampFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : File.GetLastWriteTimeUtc(path);

        var label = name.Length > TimestampFormat.Length ? name[TimestampFormat.Length..].Trim() : string.Empty;

        return new BinBackup(
            platformFolder,
            Normalize(relativePath),
            path,
            BinPayloadInstaller.DestinationPathFor(gameInstallPath, platformFolder, relativePath),
            created,
            label,
            SizeOf(path),
            IsLegacy: false);
    }

    private string FolderFor(string platformFolder, string relativePath) =>
        Path.Combine(RootPath, platformFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));

    // Two generations of the same file inside one millisecond would collide, and a numeric suffix
    // would break the chronological sort, so the timestamp is walked forward until it is free.
    private static string Reserve(string folder, string label, DateTime stamp)
    {
        var safeLabel = Sanitize(label);

        while (true)
        {
            var name = stamp.ToString(TimestampFormat, CultureInfo.InvariantCulture);

            if (!Directory.EnumerateFiles(folder, name + "*").Any())
                return Path.Combine(folder, safeLabel.Length == 0 ? name + Extension : $"{name} {safeLabel}{Extension}");

            stamp = stamp.AddMilliseconds(1);
        }
    }

    private static string Sanitize(string label)
    {
        var invalid = Path.GetInvalidFileNameChars();

        return new string([.. (label ?? string.Empty).Where(c => !invalid.Contains(c) && c != '.')]).Trim();
    }

    private static string Normalize(string relativePath) => relativePath.Replace('\\', '/');

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool SameContent(string left, string right)
    {
        try
        {
            var a = new FileInfo(left);
            var b = new FileInfo(right);

            return a.Exists && b.Exists && a.Length == b.Length
                && File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
