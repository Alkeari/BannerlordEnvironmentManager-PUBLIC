namespace BannerlordEnvironmentManager.Core.Install;

// Two folders inside the archives folder hold archives the user has finished with but kept: ARCHIVED
// for one set aside by hand from Library, INSTALLED for one kept after its mod installed. Both exist so
// the list of archives waiting to install stops showing them, so a scan or watch of the archives folder
// must never list a file under either, recursive search included, or every set-aside archive would come
// straight back as one waiting to install.
public static class ArchiveFolders
{
    public const string Archived = "ARCHIVED";

    public const string Installed = "INSTALLED";

    // The folder a set-aside archive goes to: the one named inside the archives folder when the archive
    // came from there, or beside the archive itself when it came from anywhere else (an archive picked
    // by hand, or the archives folder not set), so a move never sends a file somewhere the user never
    // pointed BEM at.
    public static string FolderFor(string archivePath, string? archivesRoot, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var root = !string.IsNullOrWhiteSpace(archivesRoot) && IsUnder(archivePath, archivesRoot)
            ? archivesRoot
            : Path.GetDirectoryName(Path.GetFullPath(archivePath))!;

        return Path.Combine(root, name);
    }

    // True for a file anywhere under ARCHIVED or INSTALLED directly inside the archives folder. A folder
    // of the same name deeper down is the user's own and is scanned like any other.
    public static bool IsSetAside(string filePath, string? archivesRoot)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(archivesRoot))
            return false;

        return IsUnder(filePath, Path.Combine(archivesRoot, Archived))
            || IsUnder(filePath, Path.Combine(archivesRoot, Installed))
            || IsUnderDotFolder(filePath, archivesRoot);
    }

    // A folder directly inside the archives folder whose name starts with a dot is kept out of the list
    // too: BEM's own staging folders, and a pool the owner keeps there on purpose (".POPULAR MODS", the
    // mods compatibility runs draw from, about five hundred archives that are not waiting to install).
    private static bool IsUnderDotFolder(string filePath, string archivesRoot)
    {
        if (!IsUnder(filePath, archivesRoot))
            return false;

        var relative = Path.GetRelativePath(Path.GetFullPath(archivesRoot), Path.GetFullPath(filePath));
        var at = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);

        return at > 0 && relative[0] == '.';
    }

    // Moves the archive into the folder, creating it, and returns where it landed. A name already taken
    // there gets " (2)", " (3)" and so on before its extension, so an older kept copy is never
    // overwritten. The file name is otherwise kept whole, since the Nexus ids in it are how BEM tells
    // where the mod came from.
    public static string MoveInto(string archivePath, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        Directory.CreateDirectory(folder);

        var name = Path.GetFileName(archivePath);
        var destination = Path.Combine(folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        for (var n = 2; File.Exists(destination) || Directory.Exists(destination); n++)
            destination = Path.Combine(folder, $"{stem} ({n}){extension}");

        File.Move(archivePath, destination);
        return destination;
    }

    private static bool IsUnder(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
