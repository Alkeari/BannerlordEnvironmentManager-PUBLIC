namespace BannerlordEnvironmentManager.Core.Instances;

public sealed record GamesRootMigrationResult(int Moved, int Left);

// Moves BEM's own folders (.tools and "BEM Recovery <timestamp>") out of the games root into BEM's
// data folder. Each is copied, every source file is checked at the destination, and only then is the
// source deleted. A folder that cannot be fully moved (in use, a reparse entry, a differing file at
// the destination) is left exactly as it was and tried again at the next start.
public static class GamesRootMigration
{
    public static GamesRootMigrationResult Run(string gamesRoot, string toolsDestination, string recoveryRoot)
    {
        if (!Directory.Exists(gamesRoot))
            return new GamesRootMigrationResult(0, 0);

        var moved = 0;
        var left = 0;

        void Handle(string source, string destination)
        {
            if (MoveVerified(source, destination))
                moved++;
            else
                left++;
        }

        var tools = InstanceLayout.LegacyToolsFolder(gamesRoot);

        if (Directory.Exists(tools))
            Handle(tools, toolsDestination);

        foreach (var folder in Directory.EnumerateDirectories(gamesRoot, InstanceLayout.RecoveryFolderPrefix + "*"))
        {
            var name = Path.GetFileName(folder);
            Handle(folder, Path.Combine(recoveryRoot, name[InstanceLayout.RecoveryFolderPrefix.Length..]));
        }

        return new GamesRootMigrationResult(moved, left);
    }

    private static bool MoveVerified(string source, string destination)
    {
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).ToList();

            if (new DirectoryInfo(source).Attributes.HasFlag(FileAttributes.ReparsePoint)
                || entries.Any(e => new FileInfo(e).Attributes.HasFlag(FileAttributes.ReparsePoint)))
                return false;

            Directory.CreateDirectory(destination);

            foreach (var entry in entries)
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, entry));

                if (Directory.Exists(entry))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                if (File.Exists(target))
                {
                    if (new FileInfo(target).Length != new FileInfo(entry).Length)
                        return false;

                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(entry, target);
            }

            foreach (var entry in entries.Where(File.Exists))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, entry));

                if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(entry).Length)
                    return false;
            }

            Directory.Delete(source, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
