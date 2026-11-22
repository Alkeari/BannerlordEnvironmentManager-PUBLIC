using BannerlordEnvironmentManager.Core.Io;

namespace BannerlordEnvironmentManager.Core.Instances;

// Putting a canonical path back is the one step in this feature that can strand a campaign, so it is
// written once and both the session's own restore and the startup repair go through it.
//
// The order is the whole point. Removing the junction first and moving the store back afterwards leaves
// the canonical path missing for the length of the move, and a move that throws there leaves it missing
// for good: the game, Steam and every other tool look for Documents\Mount and Blade II Bannerlord and
// find nothing, while a later startup repair skips it because a missing path is not a junction. So the
// data is staged beside the canonical path while the junction still stands, and only then is the
// junction swapped for the staged folder. A failure leaves either the junction or the folder, never
// nothing.
public static class CanonicalRestore
{
    public const string StagingSuffix = ".bem-restoring";

    public static string StagingPathFor(string livePath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(livePath)) + StagingSuffix;

    public static void Restore(CanonicalPathPair pair, string restingInstanceFolder)
    {
        var store = InstanceLayout.StoreFolder(restingInstanceFolder, pair.StoreFolderName);
        var staging = StagingPathFor(pair.LivePath);

        // An interrupted restore already staged the data. With the store gone, staging was a rename and
        // finishing it is the only safe move. With the store still holding data, staging was a copy
        // whose source was never removed, or one cut off partway, so the store is the whole of it: the
        // staged copy is redone from the store, because committing it would leave the store behind and
        // the next activation refuses to move the live folder onto a store that already holds data.
        if (Directory.Exists(staging))
        {
            if (!HasEntries(store))
            {
                Commit(pair.LivePath, staging);
                return;
            }

            if (!DirectoryContents.IsCoveredBy(staging, store))
                throw new IOException(
                    $"'{staging}' holds data that '{store}' does not, so neither was removed. One of the two "
                    + "is a leftover from an interrupted session and has to be dealt with by hand.");

            DirectoryMover.DeleteTree(staging);
        }

        // A live folder that is not a junction and holds data was never moved out, or was only partly
        // removed by a move that failed. A store that adds nothing to it is a duplicate and goes; a live
        // folder the store fully covers is what a failed move left and is replaced below. Two folders
        // that each hold something the other lacks are left exactly as they are, since a restore that
        // staged over them would strand a copy no later launch or startup repair could ever commit.
        if (!JunctionManager.IsJunction(pair.LivePath) && HasEntries(pair.LivePath))
        {
            if (!HasEntries(store))
                return;

            if (DirectoryContents.IsCoveredBy(store, pair.LivePath))
            {
                DirectoryMover.DeleteTree(store);
                return;
            }

            if (!DirectoryContents.IsCoveredBy(pair.LivePath, store))
                return;
        }

        if (!Directory.Exists(store))
        {
            JunctionManager.Remove(pair.LivePath);
            Directory.CreateDirectory(pair.LivePath);

            return;
        }

        var renamed = DirectoryMover.TryRename(store, staging);

        if (!renamed)
            DirectoryMover.CopyForMove(store, staging);

        Commit(pair.LivePath, staging);

        // Only now, with the canonical path holding the data again, is the copy's source removed. The
        // resting instance's store is empty at rest, and a copy left behind would block the next launch.
        if (!renamed)
            DirectoryMover.RemoveCopied(store, pair.LivePath);
    }

    // A canonical path that is missing or empty while the resting store holds data is what a restore
    // that died between removing a junction and putting the folder back leaves behind. It is not a
    // junction, so the junction check never sees it, and the saves sit in a store nothing points at.
    public static bool NeedsRestore(CanonicalPathPair pair, string restingInstanceFolder) =>
        Directory.Exists(StagingPathFor(pair.LivePath))
        || JunctionManager.IsJunction(pair.LivePath)
        || (HasEntries(InstanceLayout.StoreFolder(restingInstanceFolder, pair.StoreFolderName))
            && !HasEntries(pair.LivePath));

    public static string Describe(CanonicalPathPair pair, string restingInstanceFolder)
    {
        var store = InstanceLayout.StoreFolder(restingInstanceFolder, pair.StoreFolderName);

        if (JunctionManager.IsJunction(pair.LivePath))
            return $"'{pair.LivePath}' is still a junction to another version's data";

        if (Directory.Exists(StagingPathFor(pair.LivePath)))
            return $"'{pair.LivePath}' is waiting on the staged copy in '{StagingPathFor(pair.LivePath)}'";

        if (!Directory.Exists(pair.LivePath))
            return $"'{pair.LivePath}' is missing and its data is in '{store}'";

        return HasEntries(store)
            ? $"'{pair.LivePath}' is an ordinary folder and a second copy of it is still in '{store}'"
            : $"'{pair.LivePath}' is an ordinary folder";
    }

    private static void Commit(string livePath, string staging)
    {
        JunctionManager.Remove(livePath);

        if (Directory.Exists(livePath))
        {
            if (!HasEntries(livePath))
                Directory.Delete(livePath, recursive: false);
            else if (DirectoryContents.IsCoveredBy(livePath, staging))
                DirectoryMover.DeleteTree(livePath);
            else
                throw new IOException(
                    $"'{livePath}' already holds data the copy staged in '{staging}' does not, so the copy "
                    + "was not moved onto it. One of the two is a leftover from an interrupted session and "
                    + "has to be dealt with by hand.");
        }

        // A rename, not a copy: staging is a sibling of the canonical path, so this cannot cross volumes
        // and the canonical path is missing only for the length of one directory rename.
        Directory.Move(staging, livePath);
    }

    private static bool HasEntries(string path)
    {
        try
        {
            return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
