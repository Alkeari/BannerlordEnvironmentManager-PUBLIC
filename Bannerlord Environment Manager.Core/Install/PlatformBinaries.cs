using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Install;

public sealed record ForeignPlatformFolder(
    string ModuleFolderName,
    string ModuleFolderPath,
    string Path,
    string RelativePath,
    string PlatformFolder,
    long SizeBytes,
    int FileCount);

public sealed record PlatformCleanupResult(
    IReadOnlyList<QuarantinedItem> Quarantined,
    IReadOnlyList<string> Failed,
    int ModulesAffected,
    long BytesReclaimed);

public sealed record PlatformSkip(
    IReadOnlyList<string> PlatformFolders,
    int FileCount,
    long SizeBytes,
    IReadOnlyList<string>? KeptPlatformFolders = null)
{
    public static PlatformSkip None { get; } = new([], 0, 0);

    public bool Any => FileCount > 0;

    public IReadOnlyList<string> Kept => KeptPlatformFolders ?? [];

    public string Describe(string platformFolder) => Strings.Current.Plural(
        "Core.Install.PlatformBinaries.Skipped", FileCount, string.Join(" and ", PlatformFolders), platformFolder);

    public string DescribeKept() => Strings.Current.Plural(
        "Core.Install.PlatformBinaries.Kept", Kept.Count, string.Join(" and ", Kept));
}

public sealed record AdoptablePlatformFolder(
    string ModuleFolderName,
    string ModuleFolderPath,
    string Path,
    string TargetPath,
    string PlatformFolder,
    string TargetPlatformFolder,
    long SizeBytes,
    int FileCount);

public sealed record PlatformAdoptionResult(
    IReadOnlyList<ForeignPlatformFolder> Created,
    IReadOnlyList<string> Failed,
    long BytesCopied);

// A mod usually ships binaries for both PC platforms, and only one of them can ever be loaded. The rule
// is an allow-list of exactly the two shipping-client folder names: NavalDLC ships
// bin/Win64_Shipping_wEditor with real content, so "anything that is not mine" would take that too.
//
// A folder is foreign only when the SAME bin folder also holds the platform this install runs. Most
// mods ship Win64_Shipping_Client alone, and on Game Pass that folder is the only binaries the module
// has: removing it would leave the mod installed and inert rather than saving space. Adoption is the
// answer to that case, not removal.
public static class PlatformBinaries
{
    public const string Reason = "binaries for the other PC platform";

    public const string AdoptionReason = "a copy made for this install's platform";

    private const string BinFolderName = "bin";

    public static bool IsForeignPlatformEntry(string? entryPath, string? installPlatformFolder) =>
        ForeignPlatformFolderName(entryPath, installPlatformFolder) is not null;

    // The one parser of this string. A caller that needs to name the folder it just matched asks here
    // rather than splitting the path a second time with a weaker rule.
    public static string? ForeignPlatformFolderName(string? entryPath, string? installPlatformFolder) =>
        ForeignPlatformFolderName(entryPath, installPlatformFolder, null);

    public static string? ForeignPlatformFolderName(
        string? entryPath,
        string? installPlatformFolder,
        IReadOnlySet<string>? redundantBinFolders)
    {
        if (string.IsNullOrWhiteSpace(entryPath) || !IsKnownPlatformFolder(installPlatformFolder))
            return null;

        var segments = entryPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!segments[i].Equals(BinFolderName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!IsForeignPlatformFolderName(segments[i + 1], installPlatformFolder!))
                continue;

            if (redundantBinFolders is not null && !redundantBinFolders.Contains(string.Join('/', segments[..(i + 1)])))
                continue;

            return segments[i + 1];
        }

        return null;
    }

    // Built once from the whole entry list before anything is extracted: an archive is a stream, and
    // deciding entry by entry cannot see whether the same bin folder also carries the platform this
    // install runs.
    public static IReadOnlySet<string> RedundantBinFolders(IEnumerable<string> entryPaths, string? installPlatformFolder)
    {
        ArgumentNullException.ThrowIfNull(entryPaths);

        var redundant = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!IsKnownPlatformFolder(installPlatformFolder))
            return redundant;

        foreach (var entryPath in entryPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var segments = entryPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!segments[i].Equals(BinFolderName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (segments[i + 1].Equals(installPlatformFolder, StringComparison.OrdinalIgnoreCase))
                    redundant.Add(string.Join('/', segments[..(i + 1)]));
            }
        }

        return redundant;
    }

    public static IReadOnlyList<ForeignPlatformFolder> Find(string root, string? installPlatformFolder)
    {
        if (string.IsNullOrWhiteSpace(root) || !IsKnownPlatformFolder(installPlatformFolder))
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

        var found = new List<ForeignPlatformFolder>();

        foreach (var moduleFolder in moduleFolders.OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase))
            Collect(moduleFolder, moduleFolder, installPlatformFolder!, found);

        return found;
    }

    public static IReadOnlyList<ForeignPlatformFolder> FindInModule(string moduleFolder, string? installPlatformFolder)
    {
        if (string.IsNullOrWhiteSpace(moduleFolder) || !IsKnownPlatformFolder(installPlatformFolder))
            return [];

        var found = new List<ForeignPlatformFolder>();
        Collect(moduleFolder, moduleFolder, installPlatformFolder!, found);

        return found;
    }

    public static IReadOnlyList<ForeignPlatformFolder> FindAll(IEnumerable<string> roots, string? installPlatformFolder)
    {
        ArgumentNullException.ThrowIfNull(roots);

        return [.. roots.Where(root => !string.IsNullOrWhiteSpace(root)).SelectMany(root => Find(root, installPlatformFolder))];
    }

    // The other half of the rule. A module whose only binaries are for the other platform is inert
    // here, and the community fix on Game Pass is to give the same files the folder name this install
    // reads. BEM copies rather than renames, so the module still works if the user switches back.
    public static IReadOnlyList<AdoptablePlatformFolder> FindAdoptable(string root, string? installPlatformFolder)
    {
        if (string.IsNullOrWhiteSpace(root) || !IsKnownPlatformFolder(installPlatformFolder))
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

        var found = new List<AdoptablePlatformFolder>();

        foreach (var moduleFolder in moduleFolders.OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase))
            CollectAdoptable(moduleFolder, moduleFolder, installPlatformFolder!, found);

        return found;
    }

    public static IReadOnlyList<AdoptablePlatformFolder> FindAllAdoptable(
        IEnumerable<string> roots,
        string? installPlatformFolder)
    {
        ArgumentNullException.ThrowIfNull(roots);

        return
        [
            .. roots.Where(root => !string.IsNullOrWhiteSpace(root))
                .SelectMany(root => FindAdoptable(root, installPlatformFolder))
        ];
    }

    public static PlatformAdoptionResult Adopt(IEnumerable<AdoptablePlatformFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        var created = new List<ForeignPlatformFolder>();
        var failed = new List<string>();
        long bytes = 0;

        foreach (var folder in folders)
        {
            if (Directory.Exists(folder.TargetPath))
            {
                failed.Add(Strings.Current.Format("Core.Install.PlatformBinaries.AlreadyExists", folder.TargetPath));
                continue;
            }

            try
            {
                CopyTree(folder.Path, folder.TargetPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failed.Add(Strings.Current.Format("Core.Install.PlatformBinaries.CopyFailed", folder.Path, ex.Message));
                continue;
            }

            bytes += folder.SizeBytes;

            created.Add(new ForeignPlatformFolder(
                folder.ModuleFolderName,
                folder.ModuleFolderPath,
                folder.TargetPath,
                Path.GetRelativePath(folder.ModuleFolderPath, folder.TargetPath),
                folder.TargetPlatformFolder,
                folder.SizeBytes,
                folder.FileCount));
        }

        return new PlatformAdoptionResult(created, failed, bytes);
    }

    public static string DescribeAdoptable(
        IReadOnlyList<AdoptablePlatformFolder> folders,
        string? installPlatformFolder)
    {
        ArgumentNullException.ThrowIfNull(folders);

        if (!IsKnownPlatformFolder(installPlatformFolder))
            return Strings.Current["Core.Install.PlatformBinaries.Adoptable.PlatformUnknown"];

        if (folders.Count == 0)
            return Strings.Current["Core.Install.PlatformBinaries.Adoptable.None"];

        var modules = folders.Select(folder => folder.ModuleFolderName).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        return Strings.Current.Plural("Core.Install.PlatformBinaries.Adoptable.Some", modules, installPlatformFolder);
    }

    public static PlatformCleanupResult Quarantine(
        IEnumerable<ForeignPlatformFolder> folders,
        QuarantineStore store,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(store);

        var quarantined = new List<QuarantinedItem>();
        var failed = new List<string>();

        foreach (var folder in folders)
        {
            try
            {
                quarantined.Add(store.Store(folder.Path, folder.ModuleFolderName, folder.RelativePath, reason ?? Reason));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or DirectoryNotFoundException)
            {
                failed.Add(Strings.Current.Format("Core.Install.PlatformBinaries.MoveFailed", folder.Path, ex.Message));
            }
        }

        return new PlatformCleanupResult(
            quarantined,
            failed,
            quarantined.Select(item => item.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            quarantined.Sum(item => item.SizeBytes));
    }

    // The report counts what went away, never what was found: a folder that survives the delete is copied
    // into the game with the rest of the module, so calling it skipped would claim space nothing saved.
    public static PlatformSkip Remove(IEnumerable<ForeignPlatformFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        var removed = new List<ForeignPlatformFolder>();
        var kept = new List<ForeignPlatformFolder>();

        foreach (var folder in folders)
        {
            QuarantineStore.TryDeleteTree(folder.Path);

            if (Directory.Exists(folder.Path))
                kept.Add(folder);
            else
                removed.Add(folder);
        }

        return new PlatformSkip(
            [.. removed.Select(folder => folder.PlatformFolder).Distinct(StringComparer.OrdinalIgnoreCase)],
            removed.Sum(folder => folder.FileCount),
            removed.Sum(folder => folder.SizeBytes),
            [.. kept.Select(folder => folder.PlatformFolder).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    public static string Describe(IReadOnlyList<ForeignPlatformFolder> folders, string? installPlatformFolder)
    {
        ArgumentNullException.ThrowIfNull(folders);

        if (!IsKnownPlatformFolder(installPlatformFolder))
            return Strings.Current["Core.Install.PlatformBinaries.PlatformUnknown"];

        if (folders.Count == 0)
            return Strings.Current.Format("Core.Install.PlatformBinaries.NoneSpare", installPlatformFolder);

        var modules = folders.Select(folder => folder.ModuleFolderName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var modulePhrase = $"{modules} module" + (modules == 1 ? string.Empty : "s");

        return Strings.Current.Plural(
            "Core.Install.PlatformBinaries.Found", folders.Count, modulePhrase, installPlatformFolder);
    }

    private static bool IsKnownPlatformFolder(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && GameInstallLocator.BinaryFolders.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool IsForeignPlatformFolderName(string name, string installPlatformFolder) =>
        IsKnownPlatformFolder(name)
        && !name.Equals(installPlatformFolder, StringComparison.OrdinalIgnoreCase);

    private static void Collect(string moduleFolder, string current, string installPlatformFolder, List<ForeignPlatformFolder> found)
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

        var isBinFolder = Path.GetFileName(current).Equals(BinFolderName, StringComparison.OrdinalIgnoreCase);
        var hasOwnPlatform = isBinFolder && HasPlatformBinaries(children, installPlatformFolder);

        foreach (var child in children.OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase))
        {
            if (isBinFolder && hasOwnPlatform && IsForeignPlatformFolderName(Path.GetFileName(child), installPlatformFolder))
            {
                var (sizeBytes, fileCount) = QuarantineStore.Measure(child);

                found.Add(new ForeignPlatformFolder(
                    Path.GetFileName(moduleFolder),
                    moduleFolder,
                    child,
                    Path.GetRelativePath(moduleFolder, child),
                    Path.GetFileName(child),
                    sizeBytes,
                    fileCount));

                continue;
            }

            Collect(moduleFolder, child, installPlatformFolder, found);
        }
    }

    // An empty folder of the right name is not binaries this install can load. The whole rule for calling
    // the other platform's folder spare is that the module also ships mine, and a folder that exists and
    // holds nothing does not ship anything: counting it turned the only real binaries a module had into
    // something safe to take away, and left the mod installed and inert.
    //
    // A folder that cannot be read counts as carrying binaries, because "I could not look" must never be
    // the reason something of the user's is removed.
    private static bool HasPlatformBinaries(IEnumerable<string> children, string installPlatformFolder) =>
        children
            .Where(child => Path.GetFileName(child).Equals(installPlatformFolder, StringComparison.OrdinalIgnoreCase))
            .Any(HoldsAFileOrCannotBeRead);

    private static bool HoldsAFileOrCannotBeRead(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void CollectAdoptable(
        string moduleFolder,
        string current,
        string installPlatformFolder,
        List<AdoptablePlatformFolder> found)
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

        var isBinFolder = Path.GetFileName(current).Equals(BinFolderName, StringComparison.OrdinalIgnoreCase);
        var hasOwnPlatform = isBinFolder
            && children.Any(child => Path.GetFileName(child).Equals(installPlatformFolder, StringComparison.OrdinalIgnoreCase));

        foreach (var child in children.OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(child);

            if (isBinFolder && IsForeignPlatformFolderName(name, installPlatformFolder))
            {
                if (hasOwnPlatform)
                    continue;

                var (sizeBytes, fileCount) = QuarantineStore.Measure(child);

                if (fileCount == 0)
                    continue;

                found.Add(new AdoptablePlatformFolder(
                    Path.GetFileName(moduleFolder),
                    moduleFolder,
                    child,
                    Path.Combine(current, installPlatformFolder),
                    name,
                    installPlatformFolder,
                    sizeBytes,
                    fileCount));

                continue;
            }

            CollectAdoptable(moduleFolder, child, installPlatformFolder, found);
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        foreach (var folder in Directory.GetDirectories(source))
            CopyTree(folder, Path.Combine(destination, Path.GetFileName(folder)));
    }
}
