namespace BannerlordEnvironmentManager.Core.Io;

// Whether one folder can be thrown away because another already holds every byte of it. A failed move
// or restore leaves the same data at two paths, and the only safe way to settle which one goes is to
// prove the one going adds nothing: every folder and file in it exists in the other, with the same
// content. Anything unreadable counts as a difference, so an uncertain answer never deletes.
public static class DirectoryContents
{
    private const int BufferSize = 81920;

    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public static bool IsCoveredBy(string subset, string superset)
    {
        try
        {
            if (!Directory.Exists(subset) || !Directory.Exists(superset))
                return false;

            foreach (var directory in Directory.EnumerateDirectories(subset, "*", Walk))
            {
                if (!Directory.Exists(Path.Combine(superset, Path.GetRelativePath(subset, directory))))
                    return false;
            }

            foreach (var file in Directory.EnumerateFiles(subset, "*", Walk))
            {
                if (!SameContent(file, Path.Combine(superset, Path.GetRelativePath(subset, file))))
                    return false;
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

        if (!rightInfo.Exists || leftInfo.Length != rightInfo.Length)
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
