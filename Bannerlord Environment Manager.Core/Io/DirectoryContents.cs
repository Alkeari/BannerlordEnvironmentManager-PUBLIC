namespace BannerlordEnvironmentManager.Core.Io;

// Whether one folder can be thrown away because another already holds every byte of it. A failed move
// or restore leaves the same data at two paths, and the only safe way to settle which one goes is to
// prove the one going adds nothing: every folder and file in it exists in the other, with the same
// content. Anything unreadable counts as a difference, so an uncertain answer never deletes.
public static class DirectoryContents
{
    private const int BufferSize = 81920;

    // One level at a time with nothing skipped: skipping reparse points by attribute would also skip
    // OneDrive placeholders and deduplication stubs, which hold the user's real data, and a folder whose
    // only unique data sat in one would read as covered and be deleted. A link entry itself also belongs
    // to the source. Without proving that entry is preserved, any reparse point counts as a difference;
    // its target is never followed, including when it points back into this folder.
    private static readonly EnumerationOptions Level = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0
    };

    public static bool IsCoveredBy(string subset, string superset)
    {
        try
        {
            if (!Directory.Exists(subset) || !Directory.Exists(superset))
                return false;

            // A destination alias may point into the source itself. Reading equal bytes through it
            // proves no independent copy exists; deleting the source would remove those bytes too.
            if (File.GetAttributes(subset).HasFlag(FileAttributes.ReparsePoint)
                || File.GetAttributes(superset).HasFlag(FileAttributes.ReparsePoint))
                return false;

            var pending = new Stack<string>([subset]);

            while (pending.TryPop(out var folder))
            {
                foreach (var directory in Directory.EnumerateDirectories(folder, "*", Level))
                {
                    var info = new DirectoryInfo(directory);

                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        return false;

                    var copy = Path.Combine(superset, Path.GetRelativePath(subset, directory));
                    if (!Directory.Exists(copy) || File.GetAttributes(copy).HasFlag(FileAttributes.ReparsePoint))
                        return false;

                    pending.Push(directory);
                }

                foreach (var file in Directory.EnumerateFiles(folder, "*", Level))
                {
                    var info = new FileInfo(file);

                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        return false;

                    if (!SameContent(file, Path.Combine(superset, Path.GetRelativePath(subset, file))))
                        return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SameContent(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);

        if (!rightInfo.Exists || rightInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || leftInfo.Length != rightInfo.Length)
            return false;

        using var leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
        using var rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);

        var leftBuffer = new byte[BufferSize];
        var rightBuffer = new byte[BufferSize];

        while (true)
        {
            var read = leftStream.ReadAtLeast(leftBuffer, BufferSize, throwOnEndOfStream: false);

            if (read == 0)
                return rightStream.ReadByte() == -1;

            if (rightStream.ReadAtLeast(rightBuffer, read, throwOnEndOfStream: false) != read)
                return false;

            if (!leftBuffer.AsSpan(0, read).SequenceEqual(rightBuffer.AsSpan(0, read)))
                return false;
        }
    }
}
