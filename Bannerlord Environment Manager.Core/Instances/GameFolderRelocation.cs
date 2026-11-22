using BannerlordEnvironmentManager.Core.Game;

namespace BannerlordEnvironmentManager.Core.Instances;

// One referenced instance whose game folder was gone, and where the game was found instead.
public sealed record GameFolderRelocationChange(string InstanceId, string Name, string Was, string Now);

// A referenced install is Steam's, and Steam moves it between libraries (a new drive, a library
// removed) without BEM being asked. The record then points at a folder that no longer exists, so
// every later pass (version, DLC) reads nothing and the instance keeps whatever it said when it
// was adopted. This finds the game again so those passes read the real install.
public static class GameFolderRelocation
{
    // Where this record's game now lives, or null when it should be left as it is: a managed
    // instance, a folder that still exists, no install found, or an install another instance
    // already references (two records on one folder would make each other's state).
    public static string? For(InstanceRecord record, IEnumerable<string> candidates, IEnumerable<InstanceRecord> others)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(others);

        if (!record.GameFolderIsReferenced || string.IsNullOrWhiteSpace(record.GameFolder) || SafeExists(record.GameFolder))
            return null;

        if (!SteamMovedIt(record.GameFolder))
            return null;

        var taken = others
            .Where(other => !string.Equals(other.Id, record.Id, StringComparison.Ordinal))
            .Select(other => Normalize(other.GameFolder))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || taken.Contains(Normalize(candidate)))
                continue;

            if (GameInstallLocator.IsValidInstall(candidate))
                return candidate;
        }

        return null;
    }

    // Every Steam library's copy of the game, in Steam's own library order.
    public static IReadOnlyList<string> SteamCandidates()
    {
        if (GameInstallLocator.ReadSteamRoot() is not { } root)
            return [];

        return GameInstallLocator.FindSteamLibraries(root)
            .Select(library => Path.Combine(library, "steamapps", "common", GameInstallLocator.GameFolderName))
            .ToList();
    }

    // Only a Steam library copy is moved, and only when Steam itself let go of it: the old path is
    // <library>\steamapps\common\<game>, its drive is mounted (an unplugged or locked drive is not a
    // move), and that library no longer carries the game's app manifest. A Game Pass, Epic or GOG
    // install is never pointed at a Steam copy.
    public static bool SteamMovedIt(string oldFolder)
    {
        try
        {
            var full = Path.GetFullPath(oldFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var common = Path.GetDirectoryName(full);
            var steamapps = common is null ? null : Path.GetDirectoryName(common);

            if (!string.Equals(Path.GetFileName(full), GameInstallLocator.GameFolderName, StringComparison.OrdinalIgnoreCase)
                || common is null || steamapps is null
                || !string.Equals(Path.GetFileName(common), "common", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(steamapps), "steamapps", StringComparison.OrdinalIgnoreCase))
                return false;

            var drive = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(drive) || !SafeExists(drive))
                return false;

            return !File.Exists(Path.Combine(steamapps, "appmanifest_261550.acf"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool SafeExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
