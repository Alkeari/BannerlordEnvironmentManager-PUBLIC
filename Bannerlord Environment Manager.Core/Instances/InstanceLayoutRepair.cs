using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Instances;

// An instance whose game files sit at the top of its folder instead of inside Game\. BEM downloaded
// into the instance folder itself until 2026-09-04, so a version downloaded by that build is
// registered against a Game folder that does not exist: the version shows in the list and cannot be
// played. The files are here, and moving them one level down is a rename on the same volume.
//
// Only ever moves down into Game\, never the other way, and never touches a folder that already has
// one.
public static class InstanceLayoutRepair
{
    // What belongs to the instance rather than to the game, and stays where it is.
    private static readonly string[] NotGameFiles =
    [
        InstanceLayout.MetadataFileName,
        InstanceLayout.UserDataFolderName,
        InstanceLayout.AdoptedBackupFolderName,
        InstanceLayout.GameFolderName
    ];

    public static bool NeedsRepair(string instanceFolder)
    {
        if (string.IsNullOrWhiteSpace(instanceFolder))
            return false;

        var gameFolder = InstanceLayout.GameFolder(instanceFolder);

        // A readable version at the top and nothing under Game\ is the shape this repairs. A folder
        // with both is left alone: which one is the game is not this function's guess to make.
        return !HasGame(gameFolder) && HasGame(instanceFolder);
    }

    public static bool Repair(string instanceFolder)
    {
        if (!NeedsRepair(instanceFolder))
            return false;

        var gameFolder = InstanceLayout.GameFolder(instanceFolder);
        Directory.CreateDirectory(gameFolder);

        foreach (var entry in Directory.EnumerateFileSystemEntries(instanceFolder))
        {
            var name = Path.GetFileName(entry);

            if (NotGameFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            var destination = Path.Combine(gameFolder, name);

            if (Directory.Exists(entry))
                Directory.Move(entry, destination);
            else
                File.Move(entry, destination, overwrite: false);
        }

        return true;
    }

    // Whether this folder holds a game at all, in either shape.
    public static bool HoldsGame(string instanceFolder) =>
        HasGame(InstanceLayout.GameFolder(instanceFolder)) || HasGame(instanceFolder);

    // The folder a version should live in is named after the version, so "v1.5.2 (2)", left by a
    // build that numbered its way around a failed attempt, becomes "v1.5.2". Returns the folder it
    // ended up in, or null when it could not be moved: an occupied name is left alone rather than
    // merged into.
    public static string? RenameToVersion(string instanceFolder, string gamesRoot, string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var wanted = Path.Combine(gamesRoot, version);

        if (string.Equals(wanted, instanceFolder, StringComparison.OrdinalIgnoreCase))
            return instanceFolder;

        try
        {
            // An empty folder wearing the name is the leftover of the attempt that failed.
            if (Directory.Exists(wanted))
            {
                if (Directory.EnumerateFileSystemEntries(wanted).Any())
                    return null;

                Directory.Delete(wanted);
            }

            Directory.Move(instanceFolder, wanted);
            return wanted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HasGame(string folder)
    {
        try
        {
            return Directory.Exists(folder) && !GameVersionReader.Read(folder).IsEmpty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The version a repaired folder turns out to hold, so a record written against the wrong layout
    // can be corrected from the files rather than from what it used to claim.
    public static ModuleVersion VersionOf(string instanceFolder) =>
        GameVersionReader.Read(InstanceLayout.GameFolder(instanceFolder));
}
