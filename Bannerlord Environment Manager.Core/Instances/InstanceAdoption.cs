using BannerlordEnvironmentManager.Core.Io;

namespace BannerlordEnvironmentManager.Core.Instances;

public sealed record AdoptionReport(int FilesCopied, long BytesCopied, string BackupFolder);

public sealed record AdoptedBackup(string StoreFolderName, string Path, string RestoresTo, int Files, long Bytes);

// Adoption turns the setup already on the machine into an instance. It copies nothing into that
// instance's store, because the store of the resting instance is empty by definition: at rest the
// resting instance's data is the four canonical paths, and a store holds data only while its instance
// is parked during another one's launch. Filling the store here would leave two copies claiming to be
// the same instance and would block the next activation.
//
// What it does copy is a safety net. The originals go to adopted-backup\ and stay there until the user
// says otherwise, and that copy can be listed and put back, so adoption is not a one-way write.
public sealed class InstanceAdoption(CanonicalPathSet paths)
{
    public const string BackupFolderName = InstanceLayout.AdoptedBackupFolderName;

    public AdoptionReport Adopt(string instanceFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceFolder);

        var backup = InstanceLayout.AdoptedBackupFolder(instanceFolder);

        // A second adoption would copy whatever is live now over the originals kept from the first one,
        // which is exactly the promise the backup exists to make. It refuses instead, and the user drops
        // the old backup when they no longer want it.
        if (Read(instanceFolder).Count > 0)
            throw new IOException(
                $"'{backup}' already holds the originals from an earlier adoption of this instance. "
                + "Delete it first if those are no longer wanted; adopting again would overwrite them.");

        var files = 0;
        var bytes = 0L;

        foreach (var pair in paths.Pairs)
        {
            Directory.CreateDirectory(InstanceLayout.StoreFolder(instanceFolder, pair.StoreFolderName));

            if (!Directory.Exists(pair.LivePath))
                continue;

            var copied = DirectoryMover.Copy(pair.LivePath, Path.Combine(backup, pair.StoreFolderName));

            files += copied.Files;
            bytes += copied.Bytes;
        }

        return new AdoptionReport(files, bytes, backup);
    }

    public IReadOnlyList<AdoptedBackup> Read(string instanceFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceFolder);

        var backup = InstanceLayout.AdoptedBackupFolder(instanceFolder);
        var found = new List<AdoptedBackup>();

        foreach (var pair in paths.Pairs)
        {
            var folder = Path.Combine(backup, pair.StoreFolderName);

            if (!Directory.Exists(folder))
                continue;

            var (files, bytes) = Measure(folder);

            found.Add(new AdoptedBackup(pair.StoreFolderName, folder, pair.LivePath, files, bytes));
        }

        return found;
    }

    // The originals go back where they came from. Nothing is removed to make room: a file the backup
    // does not carry is left alone, so restoring cannot cost the user something written since adoption.
    // A canonical path that is a junction is refused outright, because writing through one would put
    // these files into a different version's store.
    public AdoptionReport Restore(string instanceFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceFolder);

        var backup = InstanceLayout.AdoptedBackupFolder(instanceFolder);
        var stored = Read(instanceFolder);

        if (stored.Count == 0)
            throw new DirectoryNotFoundException(
                $"'{backup}' holds no adopted originals, so there is nothing to put back.");

        foreach (var item in stored.Where(item => JunctionManager.IsJunction(item.RestoresTo)))
            throw new IOException(
                $"'{item.RestoresTo}' is a junction to another version's data, so nothing was put back. "
                + "Restart BEM, which returns the canonical paths to the resting instance at startup.");

        var files = 0;
        var bytes = 0L;

        foreach (var item in stored)
        {
            var copied = DirectoryMover.Copy(item.Path, item.RestoresTo);

            files += copied.Files;
            bytes += copied.Bytes;
        }

        return new AdoptionReport(files, bytes, backup);
    }

    private static (int Files, long Bytes) Measure(string folder)
    {
        var files = 0;
        var bytes = 0L;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint
                     }))
            {
                files++;
                bytes += new FileInfo(file).Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return (files, bytes);
    }
}
