using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Install;

// What a loose-file archive means when its Nexus mod id is one an installed module carries: a bug fix
// or patch that overwrites its own mod's files ("Shields Bug Fix" 8764 over "TA.Weapons and Shields"
// 8764). The archive is never matched against the whole Modules folder, because a file name such as
// shields.xml also exists in Native, and writing the fix there changes the base game.
//
// Refusal is a result, not a fallback: an archive that names a mod we cannot find, or a file that
// matches nothing or more than one file inside that mod, is stopped with a message rather than guessed.
public sealed record OwnModulePatch(IReadOnlyList<FileReplacementResolution> Files, string? Refusal)
{
    // True when a refusal says the archive patches a Nexus mod that no installed module carries (yet).
    // A collection lists a patch before or after the mod it patches in no fixed order, so a run retries
    // such a patch once after the rest are installed.
    public static bool IsOwnerMissing(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return false;

        var pattern = System.Text.RegularExpressions.Regex.Escape(Strings.Current["Core.Install.OwnModule.NoOwner"])
            .Replace("\\{0}", ".+").Replace("\\{1}", "[0-9]+").Replace("\\ ", " ");

        return System.Text.RegularExpressions.Regex.IsMatch(message, pattern, System.Text.RegularExpressions.RegexOptions.Singleline);
    }

    public static OwnModulePatch? Resolve(
        IEnumerable<string> entryPaths,
        string? archiveFileName,
        string modulesFolderPath,
        IReadOnlyDictionary<string, ModuleArchiveLink> linksByModuleId)
    {
        ArgumentNullException.ThrowIfNull(entryPaths);
        ArgumentNullException.ThrowIfNull(linksByModuleId);

        var files = ArchiveLayout.FileEntries(entryPaths);

        if (files.Count == 0)
            return null;

        var modId = NexusArchiveName.TryGetModId(archiveFileName);
        var owners = modId is { } id ? OwnersOf(id, modulesFolderPath, linksByModuleId) : [];

        // Content first: with no owner the name or links can vouch for, the installed non-official
        // modules that carry every one of the archive's relative paths are the owners. The name is
        // supporting evidence only, so "Shields Bug Fix-8764-1-0.zip" works as well as a Nexus stamp.
        if (owners.Count == 0)
        {
            var byContent = OwnersByContent(files, modulesFolderPath);

            if (byContent.Count > 1)
            {
                return new OwnModulePatch(
                    [],
                    Strings.Current.Format(
                        "Core.Install.OwnModule.ManyModules", archiveFileName ?? string.Empty, OwnerNames(byContent)));
            }

            owners = byContent;
        }

        if (owners.Count == 0)
        {
            return modId is null
                ? null
                : new OwnModulePatch(
                    [],
                    Strings.Current.Format("Core.Install.OwnModule.NoOwner", archiveFileName ?? string.Empty, modId));
        }

        var moduleFiles = owners
            .SelectMany(owner => ListFiles(owner.FolderPath).Select(file => (Owner: owner, Path: file)))
            .ToList();

        var resolutions = new List<FileReplacementResolution>();
        var problems = new List<string>();

        foreach (var entry in files)
        {
            var segments = entry.Split('/');
            var matches = new List<(ModuleManifest Owner, string Path)>();

            // The longest tail of the entry path that names a file in the module wins, so
            // "ModuleData/shields.xml" and a bare "shields.xml" both land on the same file.
            for (var skip = 0; skip < segments.Length && matches.Count == 0; skip++)
            {
                var tail = string.Join('/', segments[skip..]);
                matches = [.. moduleFiles.Where(file => EndsWithPath(file.Owner.FolderPath, file.Path, tail))];
            }

            var candidates = matches
                .Select(match => new FileReplacementCandidate(Path.GetFileName(match.Owner.FolderPath), match.Path, false))
                .ToList();

            resolutions.Add(new FileReplacementResolution(entry, segments[^1], candidates));

            if (candidates.Count == 0)
            {
                problems.Add(Strings.Current.Format("Core.Install.OwnModule.NoMatch", segments[^1], OwnerNames(owners)));
            }
            else if (candidates.Count > 1)
            {
                problems.Add(Strings.Current.Format(
                    "Core.Install.OwnModule.Ambiguous", segments[^1], candidates.Count, OwnerNames(owners)));
            }
        }

        return problems.Count > 0
            ? new OwnModulePatch([], string.Join(" ", problems))
            : new OwnModulePatch(resolutions, null);
    }

    // The installed, non-official modules this Nexus mod id belongs to, by what BEM recorded at install
    // (the archive link) and by the url the module's own manifest states.
    private static List<ModuleManifest> OwnersOf(
        int modId,
        string modulesFolderPath,
        IReadOnlyDictionary<string, ModuleArchiveLink> linksByModuleId)
    {
        if (string.IsNullOrWhiteSpace(modulesFolderPath) || !Directory.Exists(modulesFolderPath))
            return [];

        return
        [
            .. ModuleScanner.Scan(modulesFolderPath).Modules
                .Where(manifest => !manifest.IsOfficial)
                .Where(manifest =>
                    (linksByModuleId.TryGetValue(manifest.Id.Value, out var link) && link.NexusModId == modId)
                    || NexusArchiveName.TryGetModIdFromUrl(manifest.Url) == modId)
        ];
    }

    // Installed non-official modules holding a file for every entry of the archive. Empty when an
    // entry matches nothing outside the base game, so the existing matching and its confirmation run.
    private static List<ModuleManifest> OwnersByContent(IReadOnlyList<string> files, string modulesFolderPath)
    {
        if (string.IsNullOrWhiteSpace(modulesFolderPath) || !Directory.Exists(modulesFolderPath))
            return [];

        var result = new List<ModuleManifest>();

        foreach (var module in ModuleScanner.Scan(modulesFolderPath).Modules.Where(manifest => !manifest.IsOfficial && !OfficialModules.Contains(manifest.Id)))
        {
            var moduleFiles = ListFiles(module.FolderPath).ToList();

            if (files.All(entry => HasMatch(module.FolderPath, moduleFiles, entry)))
                result.Add(module);
        }

        return result;
    }

    private static bool HasMatch(string folder, List<string> moduleFiles, string entry)
    {
        var segments = entry.Split('/');

        for (var skip = 0; skip < segments.Length; skip++)
        {
            var tail = string.Join('/', segments[skip..]);

            if (moduleFiles.Any(file => EndsWithPath(folder, file, tail)))
                return true;
        }

        return false;
    }

    private static string OwnerNames(IEnumerable<ModuleManifest> owners) =>
        string.Join(", ", owners.Select(owner => Path.GetFileName(owner.FolderPath)));

    private static IEnumerable<string> ListFiles(string moduleFolder)
    {
        try
        {
            return Directory.EnumerateFiles(moduleFolder, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool EndsWithPath(string moduleFolder, string file, string tail)
    {
        var relative = Path.GetRelativePath(moduleFolder, file).Replace(Path.DirectorySeparatorChar, '/');

        return relative.Equals(tail, StringComparison.OrdinalIgnoreCase)
            || relative.EndsWith("/" + tail, StringComparison.OrdinalIgnoreCase);
    }
}
