using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Io;

// One move that crosses volumes, written once. Directory.Move is a rename and refuses two different
// roots, and BEM moves folders between drives all the time: the quarantine and the settings archive
// live under LOCALAPPDATA while the game and Documents can be anywhere, and an instance store sits in
// a games root the user puts on the drive with the most free space.
//
// Every enumeration skips reparse points. A junction inside a folder being copied would pull the whole
// of its target in, and a self-referential one would recurse until the path length threw; a junction
// inside a folder being deleted would take the user's real files with it.
public static class DirectoryMover
{
    // A copy that a delete is waiting on has to see every entry or fail. Skipping a folder the user
    // cannot enumerate would declare the copy complete, let the caller commit its record, and then start
    // deleting a source whose contents never reached the destination, and a recursive delete takes the
    // siblings with it on the way to failing on the same entry. Failing here leaves the source whole.
    // Hidden and system files stay in: EnumerationOptions drops them by default and the folders being
    // copied are the user's saves and configs, where a hidden file is still the user's file.
    // The walk is by hand, one level at a time, because a reparse point is not always a link. OneDrive
    // placeholders and deduplication stubs carry the attribute on folders and files holding the user's
    // real data; skipping them by attribute would leave them out of the copy and then delete the source.
    // Only an entry that resolves to a link target (a junction or a symbolic link) is skipped.
    private static readonly EnumerationOptions CopyLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0
    };

    // The read-only pre-pass before a delete is the opposite case. It only clears attributes, so an
    // entry it cannot reach costs nothing: Directory.Delete then fails on that entry itself and says so,
    // while throwing out of the pre-pass would refuse a tree that deletes perfectly well.
    private static readonly EnumerationOptions OneLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0
    };

    private static readonly EnumerationOptions DeleteWalk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public static void Move(string source, string destination)
    {
        if (TryRename(source, destination))
            return;

        CopyForMove(source, destination);
        RemoveCopied(source, destination);
    }

    public static bool TryRename(string source, string destination)
    {
        var parent = Path.GetDirectoryName(destination);

        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        try
        {
            Directory.Move(source, destination);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // The directory structure is copied before the files so that a folder holding nothing still exists
    // on the other side. An empty Configs folder is part of what the game expects to find.
    public static (int Files, long Bytes) Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        var folders = new List<string>();
        var pending = new Stack<string>([source]);

        while (pending.TryPop(out var folder))
        {
            folders.Add(folder);

            foreach (var child in Directory.EnumerateDirectories(folder, "*", CopyLevel))
            {
                if (!IsLink(child))
                    pending.Push(child);
            }
        }

        foreach (var folder in folders)
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, folder)));

        var files = 0;
        var bytes = 0L;

        foreach (var folder in folders)
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", CopyLevel))
            {
                if (IsLink(file))
                    continue;

                var copy = Path.Combine(destination, Path.GetRelativePath(source, file));

                InstanceFiles.Copy(file, copy);

                files++;
                bytes += new FileInfo(copy).Length;
            }
        }

        return (files, bytes);
    }

    public static void CopyForMove(string source, string destination)
    {
        try
        {
            Copy(source, destination);
        }
        catch
        {
            // A half-copied destination would hold the disk space the failed move was short of, and the
            // source is still the only complete copy, so a copy that failed leaves nothing behind.
            TryDeleteTree(destination);
            throw;
        }
    }

    // The two halves of the move are kept apart because they fail for opposite reasons. Once the copy is
    // complete the copy is the whole folder, and a delete that stops partway, which one locked DLL makes
    // ordinary while the game is running, leaves the source as the damaged one. Tidying the copy away at
    // that point would destroy the only intact version of the folder, so it stays and the caller is told
    // both are on disk rather than told the move worked.
    public static void RemoveCopied(string source, string destination)
    {
        // Copy deliberately does not follow links. Check before touching the source so an omitted
        // link, a missing entry or changed bytes cannot turn a partial copy into permanent data loss.
        if (!DirectoryContents.IsCoveredBy(source, destination))
            throw new IOException(Strings.Current.Format("Core.Io.DirectoryMover.IncompleteCopy", source, destination));

        try
        {
            DeleteTree(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"'{source}' was copied to '{destination}' but could not be removed afterwards, so both are on "
                + $"disk. The copy is complete and was kept. {ex.Message}",
                ex);
        }
    }

    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
            return;

        // A reparse point is removed as the link it is. Recursing into one would walk out of the folder
        // being deleted and into whatever it points at, which is where the user's real files are.
        // OneDrive marks the folders it manages read-only, Documents included, and Windows refuses to
        // delete a read-only folder just as it refuses a read-only file, so both are cleared.
        if (IsLink(path))
        {
            ClearReadOnly(path);
            Directory.Delete(path, recursive: false);
            return;
        }

        // OneDrive puts read-only back on a cloud folder it is still syncing, and holds it open for a
        // moment, so a delete it refuses is retried with read-only cleared again: his launch failed on
        // an empty Configs\ModSettings\Global\Matchmaker placeholder that deleted cleanly a minute later.
        for (var attempt = 1; ; attempt++)
        {
            ClearTree(path);

            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < DeleteAttempts)
            {
                Thread.Sleep(DeleteRetryDelay);
            }
        }
    }

    public const int DeleteAttempts = 6;

    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(250);

    // Walked by hand rather than with a reparse-skipping enumeration: a OneDrive cloud folder is a
    // reparse point that is not a link, and skipping it left the read-only folders inside it
    // (Configs\RBM\logs\garrison) uncleared, so his launch was refused there. A real link is cleared as
    // the link it is and never entered.
    private static void ClearTree(string path)
    {
        ClearReadOnly(path);

        if (IsLink(path))
            return;

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(path, "*", OneLevel).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
                ClearTree(entry);
            else
                ClearReadOnly(entry);
        }
    }

    public static void TryDeleteTree(string path)
    {
        try
        {
            DeleteTree(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // A reparse point that resolves to a target is a link; one that does not (a cloud placeholder, a
    // deduplication stub) holds the user's data and is treated as an ordinary entry.
    private static bool IsLink(string path)
    {
        try
        {
            var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);

            return info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);

            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
