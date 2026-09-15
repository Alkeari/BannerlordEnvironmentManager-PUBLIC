using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Install;

public sealed record ReplacedModuleFolder(QuarantinedItem Item)
{
    public string Id => Item.Id;

    public string ModuleFolderName => Path.GetFileName(Item.OriginPath);

    public string DisplayName => Strings.Current.Plural(
        "Core.Install.ReplacedModule.DisplayName",
        Item.FileCount,
        ModuleFolderName,
        Item.QuarantinedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        ClearedFileStore.DescribeSize(Item.SizeBytes),
        Item.Reason);

    public string DisplayPath =>
        Strings.Current.Format("Core.Install.ReplacedModule.DisplayPath", Item.StoredPath, Item.OriginPath);

    // A list item with no automation name of its own is announced by its ToString, and a record's own
    // ToString reads the type name and every member aloud.
    public override string ToString() => DisplayName;
}

public sealed record ReplacedModuleRestore(
    bool Restored,
    ReplacedModuleFolder? Folder,
    ReplacedModuleFolder? SetAside,
    string Error)
{
    public string Describe() => Restored
        ? Strings.Current.Format("Core.Install.ReplacedModule.Restored", Folder!.ModuleFolderName, Folder.Item.OriginPath)
          + (SetAside is null
              ? string.Empty
              : Strings.Current.Format("Core.Install.ReplacedModule.KeptNote", SetAside.Item.StoredPath))
        : Strings.Current.Format("Core.Install.ReplacedModule.NotRestored", Error);
}

public sealed record ReplacedModuleDiscard(IReadOnlyList<ReplacedModuleFolder> Removed, IReadOnlyList<string> Failed)
{
    public long SizeBytes => Removed.Sum(folder => folder.Item.SizeBytes);

    public string Describe()
    {
        var removed = Removed.Count == 0
            ? Strings.Current["Core.Install.ReplacedModule.NothingDeleted"]
            : Strings.Current.Plural(
                "Core.Install.ReplacedModule.Deleted", Removed.Count, ClearedFileStore.DescribeSize(SizeBytes));

        return Failed.Count == 0
            ? removed
            : Strings.Current.Plural(
                "Core.Install.ReplacedModule.FailedToDelete", Failed.Count, removed, string.Join("; ", Failed));
    }
}

// Installing over a module folder used to delete it outright. It is moved into the quarantine instead,
// which is the same store the shader folders use, so the copy that was there is still on disk and can
// be put back from the Library tab.
public static class ReplacedModuleQuarantine
{
    public const string Group = "replaced modules";

    public const int DefaultKeep = 5;

    private const string SetAsideReason = "set aside to put an earlier copy back";

    public static string ReasonFor(string installedFrom) => string.IsNullOrWhiteSpace(installedFrom)
        ? "replaced by an install"
        : $"replaced by installing {installedFrom}";

    public static string OverwriteReasonFor(string installedFrom) => string.IsNullOrWhiteSpace(installedFrom)
        ? "copied before an install wrote over it"
        : $"copied before installing {installedFrom} wrote over it";

    // The reverse of the two reasons above. A quarantine entry is the one place BEM already wrote down
    // which archive went over which module folder, so an install that happened before BEM recorded the
    // link at all can still be recovered from it.
    public static string? ArchiveFromReason(string? reason)
    {
        const string replaced = "replaced by installing ";
        const string overwritten = "copied before installing ";
        const string overwrittenTail = " wrote over it";

        if (string.IsNullOrWhiteSpace(reason))
            return null;

        if (reason.StartsWith(replaced, StringComparison.Ordinal))
            return Trimmed(reason[replaced.Length..]);

        return reason.StartsWith(overwritten, StringComparison.Ordinal)
            && reason.EndsWith(overwrittenTail, StringComparison.Ordinal)
                ? Trimmed(reason[overwritten.Length..^overwrittenTail.Length])
                : null;
    }

    public static ReplacedModuleFolder Store(QuarantineStore store, string moduleFolderPath, string installedFrom)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleFolderPath);

        return new ReplacedModuleFolder(store.Store(moduleFolderPath, Group, FolderName(moduleFolderPath), ReasonFor(installedFrom)));
    }

    // An install that writes over the folder rather than replacing it destroys the files it overwrites
    // just as completely, so it gets the same copy in the same list. The folder itself stays where it
    // is, because the extract is about to write into it.
    public static ReplacedModuleFolder Preserve(QuarantineStore store, string moduleFolderPath, string installedFrom)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleFolderPath);

        return new ReplacedModuleFolder(
            store.Copy(moduleFolderPath, Group, FolderName(moduleFolderPath), OverwriteReasonFor(installedFrom)));
    }

    // Newest first. Two folders stored in the same second are ordered by their place in the index,
    // which is the order they were written, so a prune never has to guess which one is older.
    //
    // One store holds every installed version's replaced folders, told apart by the absolute origin
    // each one was taken from. A null modules folder is every version, which is what a caller with no
    // install selected asks for; a caller showing or deleting one version's folders always names it.
    public static IReadOnlyList<ReplacedModuleFolder> List(QuarantineStore store, string? modulesFolderPath = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        return
        [
            .. store.Read()
                .Select((item, index) => (item, index))
                .Where(entry => entry.item.Group == Group
                    && QuarantineScope.IsUnder(entry.item.OriginPath, modulesFolderPath))
                .OrderByDescending(entry => entry.item.QuarantinedUtc)
                .ThenByDescending(entry => entry.index)
                .Select(entry => new ReplacedModuleFolder(entry.item))
        ];
    }

    public static string Describe(IReadOnlyList<ReplacedModuleFolder> folders, int keep)
    {
        ArgumentNullException.ThrowIfNull(folders);

        if (folders.Count == 0)
            return Strings.Current["Core.Install.ReplacedModule.NoneKept"];

        return Strings.Current.Plural(
            "Core.Install.ReplacedModule.Kept",
            folders.Count,
            ClearedFileStore.DescribeSize(folders.Sum(folder => folder.Item.SizeBytes)),
            keep);
    }

    // The modules folder is required rather than optional because this is a real recursive delete with
    // no undo. Counted across every version, "keep the five most recent" meant five installs on one
    // version deleted five kept folders belonging to another; the count is per version, which is what
    // the setting has always claimed.
    public static ReplacedModuleDiscard Prune(QuarantineStore store, int keep, string modulesFolderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modulesFolderPath);

        return Discard(store, [.. List(store, modulesFolderPath).Skip(Math.Max(0, keep))]);
    }

    // Named for the same reason a prune is: what is deleted has to be exactly what was listed to the
    // user, and the list they were shown is one version's.
    public static ReplacedModuleDiscard DiscardAll(QuarantineStore store, string modulesFolderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modulesFolderPath);

        return Discard(store, List(store, modulesFolderPath));
    }

    public static ReplacedModuleRestore Restore(QuarantineStore store, string id)
    {
        ArgumentNullException.ThrowIfNull(store);

        var folder = List(store).FirstOrDefault(entry => entry.Id == id);

        if (folder is null)
            return new ReplacedModuleRestore(false, null, null, Strings.Current["Core.Install.ReplacedModule.NoLongerQuarantined"]);

        if (!Directory.Exists(folder.Item.StoredPath))
        {
            return new ReplacedModuleRestore(
                false, folder, null,
                Strings.Current.Format("Core.Install.ReplacedModule.KeptCopyGone", folder.Item.StoredPath));
        }

        ReplacedModuleFolder? setAside = null;

        // The module is usually installed again at the origin. Quarantining that folder rather than
        // renaming it beside itself keeps the Modules folder free of a second copy the game would load.
        if (Directory.Exists(folder.Item.OriginPath))
        {
            try
            {
                setAside = new ReplacedModuleFolder(store.Store(folder.Item.OriginPath, Group, folder.ModuleFolderName, SetAsideReason));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or DirectoryNotFoundException)
            {
                return new ReplacedModuleRestore(
                    false,
                    folder,
                    null,
                    Strings.Current.Format(
                        "Core.Install.ReplacedModule.CouldNotSetAside", folder.ModuleFolderName, ex.Message));
            }
        }

        if (store.Restore(folder.Id))
            return new ReplacedModuleRestore(true, folder, setAside, string.Empty);

        if (setAside is not null && store.Restore(setAside.Id))
        {
            return new ReplacedModuleRestore(
                false,
                folder,
                null,
                Strings.Current["Core.Install.ReplacedModule.RevertedToOrigin"]);
        }

        return new ReplacedModuleRestore(
            false,
            folder,
            setAside,
            Strings.Current.Format(
                "Core.Install.ReplacedModule.BothCopiesStranded", folder.Item.OriginPath, store.RootPath));
    }

    private static ReplacedModuleDiscard Discard(QuarantineStore store, IReadOnlyList<ReplacedModuleFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(store);

        var removed = new List<ReplacedModuleFolder>();
        var failed = new List<string>();

        foreach (var folder in folders)
        {
            try
            {
                if (store.Discard(folder.Id))
                    removed.Add(folder);
                else
                {
                    failed.Add(Strings.Current.Format(
                        "Core.Install.ReplacedModule.OutsideQuarantine", folder.ModuleFolderName));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(Strings.Current.Format(
                    "Core.Install.ReplacedModule.DeleteFailed", folder.ModuleFolderName, ex.Message));
            }
        }

        return new ReplacedModuleDiscard(removed, failed);
    }

    private static string? Trimmed(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FolderName(string path) =>
        Path.GetFileName(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}
