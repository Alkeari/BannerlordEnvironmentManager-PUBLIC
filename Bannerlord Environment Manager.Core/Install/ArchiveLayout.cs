using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Install;

public sealed record BinPayloadFile(string EntryPath, string RelativePath);

public sealed record BinPayload(string PlatformFolder, IReadOnlyList<BinPayloadFile> Files)
{
    public IReadOnlyList<string> InjectorProxies { get; } =
        Files.Select(file => file.RelativePath.Split('/')[^1])
            .Where(ArchiveLayout.IsInjectorProxy)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

public static class ArchiveLayout
{
    private const string BinFolderName = "bin";
    private const string SubModuleManifestName = "SubModule.xml";
    private const int TopLevelNamesShown = 6;

    // Every code mod ships its own <ModFolder>/bin/<platform>/<ModName>.dll, so the presence of a
    // bin/<platform> subtree is not enough: only a subtree that no SubModule.xml governs belongs in
    // the game's own bin folder.
    public static IReadOnlyList<BinPayload> FindBinPayloads(IEnumerable<string> entryPaths)
    {
        var files = NormalizeFileEntries(entryPaths);
        var moduleRoots = files
            .Where(path => LastSegment(path).Equals(SubModuleManifestName, StringComparison.OrdinalIgnoreCase))
            .Select(ParentPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var byPlatform = new Dictionary<string, List<BinPayloadFile>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var path in files)
        {
            if (SplitBinPayload(path) is not { } split)
                continue;

            var (container, platform, relative) = split;

            if (IsGovernedByModule(container, moduleRoots))
                continue;

            if (!byPlatform.TryGetValue(platform, out var payloadFiles))
            {
                payloadFiles = [];
                byPlatform[platform] = payloadFiles;
                order.Add(platform);
            }

            payloadFiles.Add(new BinPayloadFile(path, relative));
        }

        return order.Select(platform => new BinPayload(platform, byPlatform[platform])).ToList();
    }

    // A Steam install must not receive Game Pass binaries, but an install whose platform
    // cannot be read is better served by everything the archive offers than by nothing.
    public static IReadOnlyList<BinPayload> ForPlatform(IEnumerable<BinPayload> payloads, string? platformFolder) =>
        string.IsNullOrWhiteSpace(platformFolder)
            ? payloads.ToList()
            : payloads.Where(payload => payload.PlatformFolder.Equals(platformFolder, StringComparison.OrdinalIgnoreCase)).ToList();

    public static IReadOnlyList<string> InjectorProxyNames { get; } =
    [
        "d3d9.dll",
        "d3d11.dll",
        "dinput8.dll",
        "dxgi.dll",
        "opengl32.dll",
        "winmm.dll"
    ];

    public static bool IsInjectorProxy(string fileName) =>
        InjectorProxyNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    public static string DescribeUnrecognizedLayout(IEnumerable<string> entryPaths)
    {
        var topLevel = NormalizeFileEntries(entryPaths)
            .Select(path => path.Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var shown = string.Join(", ", topLevel.Take(TopLevelNamesShown));
        var found = topLevel.Count switch
        {
            0 => Strings.Current["Core.Install.Layout.NothingFound"],
            > TopLevelNamesShown => shown + " "
                + Strings.Current.Plural("Core.Install.Layout.AndMore", topLevel.Count - TopLevelNamesShown),
            _ => shown
        };

        var expected = string.Join(" or ", GameInstallLocator.BinaryFolders.Select(folder => $"{BinFolderName}/{folder}"));

        return Strings.Current.Format("Core.Install.Layout.Unrecognized", found, SubModuleManifestName, expected);
    }

    public static IReadOnlyList<string> FileEntries(IEnumerable<string> entryPaths) =>
        NormalizeFileEntries(entryPaths);

    // Zip writers are inconsistent about the trailing slash on directory entries: the Dro Lighting
    // archive stores TextureMods without one. A path that another path sits under is a directory
    // whatever the separator says.
    private static List<string> NormalizeFileEntries(IEnumerable<string> entryPaths)
    {
        var paths = entryPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/').Trim('/'))
            .Where(path => path.Length > 0)
            .ToList();

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            for (var parent = ParentPath(path); parent.Length > 0; parent = ParentPath(parent))
            {
                directories.Add(parent);
            }
        }

        return paths.Where(path => !directories.Contains(path)).ToList();
    }

    private static (string Container, string Platform, string Relative)? SplitBinPayload(string path)
    {
        var segments = path.Split('/');

        for (var i = 0; i < segments.Length - 2; i++)
        {
            if (!segments[i].Equals(BinFolderName, StringComparison.OrdinalIgnoreCase))
                continue;

            var platform = GameInstallLocator.BinaryFolders
                .FirstOrDefault(folder => folder.Equals(segments[i + 1], StringComparison.OrdinalIgnoreCase));

            if (platform is null)
                continue;

            return (string.Join('/', segments[..i]), platform, string.Join('/', segments[(i + 2)..]));
        }

        return null;
    }

    private static bool IsGovernedByModule(string container, HashSet<string> moduleRoots)
    {
        var current = container;

        while (true)
        {
            if (moduleRoots.Contains(current))
                return true;

            if (current.Length == 0)
                return false;

            current = ParentPath(current);
        }
    }

    private static string ParentPath(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : path[..lastSlash];
    }

    private static string LastSegment(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash < 0 ? path : path[(lastSlash + 1)..];
    }
}
