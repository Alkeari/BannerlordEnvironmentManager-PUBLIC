namespace BannerlordEnvironmentManager.Core.Modules;

// A duplicate-id folder that is only a misplaced copy, never a module of its own: its name is not the
// module id, another folder in the same group is named for the id, and it holds nothing but a
// SubModule.xml byte-for-byte the same as that folder's. An archive whose top level was ModuleData or
// bin, extracted one level too high, leaves exactly this (Modules\ModuleData\SubModule.xml declaring
// FreeLords beside the real Modules\FreeLords on 2026-11-12). Removing such a folder loses nothing,
// so BEM offers it; any other duplicate stays the owner's to decide.
//
// The game keeps the first folder it finds for an id and skips every later one (ModuleHelper.
// InitializeModules guards its dictionary with ContainsKey), so a copy found after the real folder is
// inert: odd, but nothing to warn about, as the owner pointed out. Only a copy found first matters,
// since the game would then load a folder with no code in it. Harmless() answers that.
public static class StrayModuleCopy
{
    private const string Manifest = "SubModule.xml";

    public static IReadOnlyList<DuplicateIdFolder> In(DuplicateModuleIdGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var strays = Strays(group.Id, [.. group.Folders.Select(f => f.FolderPath)]);
        return [.. group.Folders.Where(f => strays.Contains(f.FolderPath))];
    }

    // The folders, in scan order, that are stray copies found after the folder named for the id.
    public static IReadOnlySet<string> Harmless(ModuleId id, IReadOnlyList<string> folderPaths)
    {
        ArgumentNullException.ThrowIfNull(folderPaths);

        var named = IndexOfNamed(id, folderPaths);
        var strays = Strays(id, folderPaths);
        return new HashSet<string>(folderPaths.Where((path, i) => i > named && strays.Contains(path)));
    }

    private static int IndexOfNamed(ModuleId id, IReadOnlyList<string> folderPaths)
    {
        for (var i = 0; i < folderPaths.Count; i++)
        {
            var name = Path.GetFileName(folderPaths[i].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(name, id.Value, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static HashSet<string> Strays(ModuleId id, IReadOnlyList<string> folderPaths)
    {
        var named = IndexOfNamed(id, folderPaths);

        if (named < 0)
            return [];

        byte[] reference;

        try
        {
            reference = File.ReadAllBytes(Path.Combine(folderPaths[named], Manifest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return [.. folderPaths.Where((path, i) => i != named && IsOnlyACopyOf(path, reference))];
    }

    private static bool IsOnlyACopyOf(string folder, byte[] reference)
    {
        try
        {
            var entries = Directory.GetFileSystemEntries(folder);

            return entries.Length == 1
                && string.Equals(Path.GetFileName(entries[0]), Manifest, StringComparison.OrdinalIgnoreCase)
                && File.Exists(entries[0])
                && !new FileInfo(entries[0]).Attributes.HasFlag(FileAttributes.ReparsePoint)
                && !new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint)
                && File.ReadAllBytes(entries[0]).AsSpan().SequenceEqual(reference);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
