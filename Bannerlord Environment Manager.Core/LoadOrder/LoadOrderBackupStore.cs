using System.Globalization;
using System.Xml;
using System.Xml.Linq;

using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// ModuleCount and EnabledCount are nullable because a truncated LauncherData.xml is exactly the kind
// of file worth having a backup of, and a backup that cannot be parsed still has to be listed and
// still has to be restorable.
public sealed record LoadOrderBackup(
    string Path,
    DateTime CreatedUtc,
    string Label,
    int? ModuleCount,
    int? EnabledCount)
{
    public string ContentSummary => ModuleCount is null || EnabledCount is null
        ? Strings.Current["Core.LoadOrder.Backup.Unreadable"]
        : Strings.Current.Plural("Core.LoadOrder.ModuleCountSummary", ModuleCount.Value, EnabledCount.Value);

    public string DisplayName =>
        $"{CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} - {Label} - {ContentSummary}";
}

public sealed class LoadOrderBackupStore
{
    public const int DefaultRetention = 10;

    private const string TimestampFormat = "yyyy-MM-dd HH-mm-ss-fff";

    private const string Extension = ".xml";

    public LoadOrderBackupStore(string rootPath, int retention = DefaultRetention)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retention, 1);

        RootPath = rootPath;
        Retention = retention;
    }

    public string RootPath { get; }

    public int Retention { get; }

    // Per version, like every other opinion BEM holds about a load order. One shared ring let a
    // backup taken from one version's LauncherData.xml be restored straight onto another version's
    // file, which is the load order loss this whole feature exists to prevent.
    public static string GetDefaultRoot(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, "backups");

    // Repeatedly saving an order that has not changed would otherwise push every earlier state out of
    // a ten-slot ring within one session, so an identical copy is reported rather than written.
    public LoadOrderBackup? Capture(string sourcePath, string label)
    {
        if (!File.Exists(sourcePath))
            return null;

        Directory.CreateDirectory(RootPath);

        var newest = List().FirstOrDefault();

        if (newest is not null && SameContent(newest.Path, sourcePath))
            return newest;

        var path = Reserve(label);
        File.Copy(sourcePath, path, overwrite: false);

        Prune();

        return Describe(path);
    }

    public IReadOnlyList<LoadOrderBackup> List()
    {
        if (!Directory.Exists(RootPath))
            return [];

        return [.. Directory.EnumerateFiles(RootPath, "*" + Extension)
            .Select(Describe)
            .OrderByDescending(b => b.CreatedUtc)
            .ThenByDescending(b => b.Path, StringComparer.OrdinalIgnoreCase)];
    }

    public LoadOrderSnapshot Read(LoadOrderBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);

        var entries = ReadEntries(backup.Path);

        return entries is null
            ? LoadOrderSnapshot.Empty
            : new LoadOrderSnapshot([.. entries]);
    }

    // The state being replaced is captured first, so restoring the wrong backup is itself undoable.
    // Nothing is overwritten until that capture has succeeded.
    public LoadOrderBackup? Restore(string backupPath, string targetPath)
    {
        if (!File.Exists(backupPath))
            throw new FileNotFoundException($"'{backupPath}' is no longer there to restore.", backupPath);

        var safety = Capture(targetPath, "before restore");

        var directory = System.IO.Path.GetDirectoryName(targetPath);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.Copy(backupPath, targetPath, overwrite: true);

        return safety;
    }

    // A backup the user wants rid of, refused for any path outside the backup folder. How the file goes
    // is the caller's, so the app can send it to the Recycle Bin without dragging Windows into Core.
    public bool Discard(string backupPath, Action<string> removeFile)
    {
        ArgumentNullException.ThrowIfNull(removeFile);

        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            return false;

        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(backupPath));

        if (folder is null ||
            !string.Equals(folder, System.IO.Path.GetFullPath(RootPath), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        removeFile(backupPath);

        return true;
    }

    // The only sanctioned deletion of user data in BEM, and the reason the default limit is generous.
    private void Prune()
    {
        foreach (var stale in List().Skip(Retention))
        {
            try
            {
                File.Delete(stale.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A backup that will not delete is a full ring, not lost data. The next capture retries.
            }
        }
    }

    // Two captures inside the same millisecond would collide, and a numeric suffix would break the
    // chronological sort the prune depends on, so the timestamp is walked forward until it is free.
    private string Reserve(string label)
    {
        var safeLabel = Sanitize(label);
        var stamp = DateTime.UtcNow;

        while (true)
        {
            var name = stamp.ToString(TimestampFormat, CultureInfo.InvariantCulture);
            var path = System.IO.Path.Combine(
                RootPath, safeLabel.Length == 0 ? name + Extension : $"{name} {safeLabel}{Extension}");

            if (!File.Exists(path))
                return path;

            stamp = stamp.AddMilliseconds(1);
        }
    }

    private static string Sanitize(string label)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();

        return new string([.. (label ?? string.Empty)
            .Where(c => !invalid.Contains(c) && c != '.')]).Trim();
    }

    private static LoadOrderBackup Describe(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);

        var created = name.Length >= TimestampFormat.Length
            && DateTime.TryParseExact(
                name[..TimestampFormat.Length], TimestampFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : File.GetLastWriteTimeUtc(path);

        var label = name.Length > TimestampFormat.Length ? name[TimestampFormat.Length..].Trim() : string.Empty;

        var entries = ReadEntries(path);

        return new LoadOrderBackup(
            path, created, label, entries?.Count, entries?.Count(e => e.IsEnabled));
    }

    private static List<LoadOrderSnapshotEntry>? ReadEntries(string path)
    {
        try
        {
            var modDatas = XDocument.Load(path).Root
                ?.Element("SingleplayerData")
                ?.Element("ModDatas");

            if (modDatas is null)
                return null;

            return [.. modDatas.Elements("UserModData")
                .Select(e => new LoadOrderSnapshotEntry(
                    e.Element("Id")?.Value?.Trim() ?? string.Empty,
                    string.Equals(e.Element("IsSelected")?.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase)))
                .Where(e => e.Id.Length > 0)];
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
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
