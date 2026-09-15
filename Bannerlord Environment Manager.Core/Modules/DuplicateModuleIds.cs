namespace BannerlordEnvironmentManager.Core.Modules;

// One folder that declares a module id another folder under the same root also declares.
// LastWriteUtc is what tells a freshly staged build apart from the install that was already there,
// and it is null when the folder's metadata could not be read, which is a different answer from a
// folder that was never written. Reading it opens nothing inside the folder, so it cannot be what
// blocks a rename.
public sealed record DuplicateIdFolder(string FolderName, string FolderPath, DateTime? LastWriteUtc);

public sealed record DuplicateModuleIdGroup(ModuleId Id, IReadOnlyList<DuplicateIdFolder> Folders);

// The one answer to "which module ids are declared by more than one folder under the root that was
// scanned". It used to be computed twice: ModuleEnvironment.Merge grouped the merged manifest list
// for the load-order validator, and nothing else asked, so a folder pair under Modules never reached
// a screen carrying the evidence a person needs to act on it. Both callers come through here now.
//
// This is a different question from ModuleScanner's Shadowed list, which records a Workshop copy that
// was dropped because Modules already supplied that id. That copy is inert and the game still starts;
// two folders under Modules stop the game loading at all.
public static class DuplicateModuleIds
{
    // The grouping alone, with no filesystem read: Merge runs on every rescan and only needs to know
    // which ids repeat, not when each folder was last written.
    public static IReadOnlyList<(ModuleId Id, IReadOnlyList<ModuleManifest> Folders)> Group(
        IReadOnlyList<ModuleManifest> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        return
        [
            .. modules
                .GroupBy(m => m.Id)
                .Where(g => g.Count() > 1)
                .Select(g => (g.Key, (IReadOnlyList<ModuleManifest>)[.. g]))
        ];
    }

    // Scan order is preserved, both between groups and within one, because the first folder in a
    // group is the manifest every other reader already treats as the one in use. Sorting by
    // timestamp here would silently move that choice; the timestamps are reported instead, which is
    // what lets a developer read the pair for themselves.
    public static IReadOnlyList<DuplicateModuleIdGroup> Find(
        IReadOnlyList<ModuleManifest> modules,
        Func<string, DateTime?>? lastWriteUtc = null)
    {
        var read = lastWriteUtc ?? ReadLastWriteUtc;

        return
        [
            .. Group(modules).Select(group => new DuplicateModuleIdGroup(
                group.Id,
                [
                    .. group.Folders.Select(m => new DuplicateIdFolder(
                        FolderNameOf(m.FolderPath),
                        m.FolderPath,
                        read(m.FolderPath)))
                ]))
        ];
    }

    private static string FolderNameOf(string folderPath) =>
        Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static DateTime? ReadLastWriteUtc(string folderPath)
    {
        try
        {
            return Directory.Exists(folderPath) ? Directory.GetLastWriteTimeUtc(folderPath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
