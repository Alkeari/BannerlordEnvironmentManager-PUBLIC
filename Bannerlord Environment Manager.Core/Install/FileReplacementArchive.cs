namespace BannerlordEnvironmentManager.Core.Install;

public sealed record FileReplacementCandidate(string ModuleFolderName, string TargetPath, bool IsOfficialModule);

public sealed record FileReplacementResolution(
    string EntryPath,
    string FileName,
    IReadOnlyList<FileReplacementCandidate> Candidates)
{
    public bool IsAmbiguous => Candidates.Count > 1;

    public FileReplacementCandidate? Target => Candidates.Count == 1 ? Candidates[0] : null;
}

// Some mods ship no module at all: just the file they want you to drop over an existing one. They are
// indistinguishable from an unrecognized archive by layout alone, so the file names are what resolve
// them.
public static class FileReplacementArchive
{
    private const string ModuleDataFolderName = "ModuleData";
    private const string SubModuleManifestName = "SubModule.xml";

    public static bool IsLooseFileArchive(IEnumerable<string> entryPaths)
    {
        var files = ArchiveLayout.FileEntries(entryPaths);

        if (files.Count == 0)
            return false;

        if (files.Any(path => LastSegment(path).Equals(SubModuleManifestName, StringComparison.OrdinalIgnoreCase)))
            return false;

        return ArchiveLayout.FindBinPayloads(files).Count == 0;
    }

    public static IReadOnlyList<FileReplacementResolution> Resolve(
        IEnumerable<string> entryPaths,
        string modulesFolderPath,
        IReadOnlyCollection<string>? officialModuleFolders = null)
    {
        var files = ArchiveLayout.FileEntries(entryPaths);

        if (files.Count == 0)
            return [];

        var index = IndexModuleData(modulesFolderPath, officialModuleFolders);

        return
        [
            .. files.Select(path =>
            {
                var fileName = LastSegment(path);

                return new FileReplacementResolution(
                    path,
                    fileName,
                    index.TryGetValue(fileName, out var candidates) ? candidates : []);
            })
        ];
    }

    private static Dictionary<string, List<FileReplacementCandidate>> IndexModuleData(
        string modulesFolderPath,
        IReadOnlyCollection<string>? officialModuleFolders)
    {
        var index = new Dictionary<string, List<FileReplacementCandidate>>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(modulesFolderPath))
            return index;

        string[] moduleFolders;

        try
        {
            moduleFolders = Directory.GetDirectories(modulesFolderPath);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return index;
        }

        foreach (var moduleFolder in moduleFolders.OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase))
        {
            var moduleData = Path.Combine(moduleFolder, ModuleDataFolderName);

            if (!Directory.Exists(moduleData))
                continue;

            var moduleName = Path.GetFileName(moduleFolder);
            var isOfficial = officialModuleFolders?.Contains(moduleName, StringComparer.OrdinalIgnoreCase) ?? false;

            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles(moduleData, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);

                if (!index.TryGetValue(fileName, out var candidates))
                {
                    candidates = [];
                    index[fileName] = candidates;
                }

                candidates.Add(new FileReplacementCandidate(moduleName, file, isOfficial));
            }
        }

        return index;
    }

    private static string LastSegment(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash < 0 ? path : path[(lastSlash + 1)..];
    }
}
