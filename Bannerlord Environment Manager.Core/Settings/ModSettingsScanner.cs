using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Settings;

public sealed record ModSettingsFolder(
    string Name,
    string RelativePath,
    string FullPath,
    long SizeBytes,
    int FileCount,
    DateTimeOffset LastWriteUtc);

public static class ModSettingsScanner
{
    public const string GlobalFolderName = "Global";

    public static string DefaultRoot => DefaultRootFor();

    // DefaultRoot is a property, so a caller that needs the active instance's store rather than the
    // machine default calls this instead.
    public static string DefaultRootFor(InstanceDataRoot? dataRoot = null) =>
        Path.Combine((dataRoot ?? InstanceDataRoot.ForMachine()).Configs, "ModSettings");

    // Loose files sitting directly in the root or directly in Global belong to no folder, so nothing
    // here ever offers to move them.
    public static IReadOnlyList<ModSettingsFolder> Scan(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return [];

        var folders = new List<ModSettingsFolder>();

        foreach (var directory in EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);

            if (string.Equals(name, GlobalFolderName, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var child in EnumerateDirectories(directory))
                    folders.Add(Describe(child, Path.Combine(GlobalFolderName, Path.GetFileName(child))));

                continue;
            }

            folders.Add(Describe(directory, name));
        }

        return [.. folders.OrderBy(folder => folder.RelativePath, StringComparer.OrdinalIgnoreCase)];
    }

    // A folder with no file anywhere under it holds no settings, so it is not a folder BEM failed to
    // identify: it is an empty folder, and that is the whole answer about it.
    public static bool IsEmpty(ModSettingsFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        return folder.FileCount == 0;
    }

    public static ModSettingsFolder? Find(string root, string relativePath) =>
        Scan(root).FirstOrDefault(folder =>
            string.Equals(folder.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

    public static (long SizeBytes, int FileCount) Measure(string directory)
    {
        long sizeBytes = 0;
        var fileCount = 0;

        foreach (var file in EnumerateFiles(directory))
        {
            try
            {
                sizeBytes += new FileInfo(file).Length;
                fileCount++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return (sizeBytes, fileCount);
    }

    public static IEnumerable<string> EnumerateFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        try
        {
            return Directory.EnumerateFiles(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // EnumerateFiles ignores what it cannot get into, which is right for measuring a folder and wrong
    // for a credential check: a file inside an unreadable subfolder is never scanned and never counted
    // as unscanned either, so an all-clear could silently cover it. This names those folders so the
    // caller can say it could not look rather than that it found nothing.
    public static IReadOnlyList<string> FindUnreadableFolders(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        var unreadable = new List<string>();
        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            try
            {
                // Any() is one MoveNext, which is where an unreadable folder actually throws.
                _ = Directory.EnumerateFiles(current).Any();

                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(current);
            }
        }

        return [.. unreadable.Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static ModSettingsFolder Describe(string fullPath, string relativePath)
    {
        var (sizeBytes, fileCount) = Measure(fullPath);

        return new ModSettingsFolder(
            Path.GetFileName(fullPath),
            relativePath,
            Path.GetFullPath(fullPath),
            sizeBytes,
            fileCount,
            LastWrite(fullPath));
    }

    private static DateTimeOffset LastWrite(string path)
    {
        try
        {
            return new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
