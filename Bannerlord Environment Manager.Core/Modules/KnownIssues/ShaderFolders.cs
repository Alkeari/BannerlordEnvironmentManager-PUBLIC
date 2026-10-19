namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public sealed record ShaderFolder(
    string ModuleFolderName,
    string ModuleFolderPath,
    string Path,
    string RelativePath,
    long SizeBytes,
    int FileCount);

public static class ShaderFolders
{
    public const string FolderName = "shaders";

    // The engine rebuilds its own shader output. TaleWorlds.Native.dll carries the literal string
    // "A change in your active mods was detected. Deleting the runtime shader cache", so these paths
    // are already self-healing and moving one only costs the user a recompile.
    private const string EngineCacheFolderName = "ShaderCache";

    // Not one of the data-root call sites. These are folder names matched against whatever a scan root
    // happens to contain, so that a shaders folder sitting under the game's own name is recognized as
    // engine-owned; nothing here builds a path to the game's user data, and an instance data root would
    // have nothing to give it.
    private static readonly string[] GameRootFolderNames =
    [
        "Mount & Blade II Bannerlord",
        "Mount and Blade II Bannerlord"
    ];

    public static IReadOnlyList<ShaderFolder> Find(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return [];

        string[] moduleFolders;

        try
        {
            moduleFolders = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var found = new List<ShaderFolder>();

        foreach (var moduleFolder in moduleFolders.OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase))
            Collect(moduleFolder, moduleFolder, found);

        return found;
    }

    public static IReadOnlyList<ShaderFolder> FindAll(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        return [.. roots.Where(root => !string.IsNullOrWhiteSpace(root)).SelectMany(Find)];
    }

    public static bool IsEngineOwned(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Equals(EngineCacheFolderName, StringComparison.OrdinalIgnoreCase))
                return true;

            if (i > 0
                && segments[i].Equals(FolderName, StringComparison.OrdinalIgnoreCase)
                && GameRootFolderNames.Contains(segments[i - 1], StringComparer.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void Collect(string moduleFolder, string current, List<ShaderFolder> found)
    {
        string[] children;

        try
        {
            children = Directory.GetDirectories(current);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children.OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(child);

            if (name.Equals(EngineCacheFolderName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!name.Equals(FolderName, StringComparison.OrdinalIgnoreCase))
            {
                Collect(moduleFolder, child, found);
                continue;
            }

            if (IsEngineOwned(child))
                continue;

            var (sizeBytes, fileCount) = Measure(child);

            // The whole folder moves, so a shaders folder nested inside one is already accounted for
            // and reporting it separately would offer the user a second action that cannot be taken.
            found.Add(new ShaderFolder(
                Path.GetFileName(moduleFolder),
                moduleFolder,
                child,
                Path.GetRelativePath(moduleFolder, child),
                sizeBytes,
                fileCount));
        }
    }

    private static (long SizeBytes, int FileCount) Measure(string directory)
    {
        long sizeBytes = 0;
        var fileCount = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return (sizeBytes, fileCount);
    }
}
