using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.Localization;
using Microsoft.Win32;

namespace BannerlordEnvironmentManager.Core.Game;

public sealed record InstallSearch(string? Path, IReadOnlyList<string> Unreadable)
{
    public static InstallSearch Nothing { get; } = new(null, []);

    public bool Found => Path is not null;
}

public static partial class GameInstallLocator
{
    private const string GameFolderName = "Mount & Blade II Bannerlord";

    // Game Pass installs the game as <root>\<title>\Content, where <title> is the store name with the
    // characters a folder cannot hold replaced. The name is never assumed: every child of the
    // container is offered to IsValidInstall instead.
    private const string GamePassContentFolderName = "Content";

    [GeneratedRegex("""^\s*"path"\s+"(?<path>[^"]+)"\s*$""", RegexOptions.Multiline)]
    private static partial Regex LibraryPathPattern { get; }

    public const string StandardBinaryFolder = "Win64_Shipping_Client";

    // The Game Pass build ships a .NET Core binary directory named
    // Gaming.Desktop.x64_Shipping_Client rather than Win64_Shipping_Client, so a moddable
    // install has to accept either.
    public const string GamePassBinaryFolder = "Gaming.Desktop.x64_Shipping_Client";

    public static IReadOnlyList<string> BinaryFolders { get; } =
    [
        StandardBinaryFolder,
        GamePassBinaryFolder
    ];

    // Where an Xbox app install can sit, relative to a drive root. XboxGames is where the modern Xbox
    // app puts games the user can reach; the two WindowsApps folders are the older locations and
    // usually refuse to be listed at all, which is reported rather than swallowed.
    public static IReadOnlyList<string> GamePassContainerFolders { get; } =
    [
        "XboxGames",
        Path.Combine("Program Files", "ModifiableWindowsApps"),
        Path.Combine("Program Files", "WindowsApps"),
        "WindowsApps"
    ];

    public static IReadOnlyList<string> GetBinaryFolders(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return [];

        return [.. BinaryFolders.Where(folder => SafeDirectoryExists(Path.Combine(path, "bin", folder)))];
    }

    public static string? GetBinaryFolder(string path) => GetBinaryFolders(path).FirstOrDefault();

    public static PlatformDetection DetectPlatform(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return PlatformDetection.Unknown;

        var present = GetBinaryFolders(path);

        return present.Count switch
        {
            0 => new PlatformDetection(
                null,
                present,
                PlatformConfidence.None,
                Strings.Current["Core.Game.PlatformDetection.None"]),
            1 => new PlatformDetection(
                present[0],
                present,
                PlatformConfidence.Confident,
                Strings.Current.Format(
                    "Core.Game.PlatformDetection.Confident", PlatformDetection.DescribePlatform(present[0]), present[0])),
            _ => new PlatformDetection(
                present[0],
                present,
                PlatformConfidence.Ambiguous,
                Strings.Current.Format(
                    "Core.Game.PlatformDetection.Ambiguous",
                    string.Join(" and ", present.Select(folder => $"bin\\{folder}"))))
        };
    }

    public static bool IsValidInstall(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && SafeDirectoryExists(Path.Combine(path, "Modules"))
        && GetBinaryFolders(path).Count > 0;

    public static IReadOnlyList<string> FindSteamLibraries(string steamRoot)
    {
        var manifest = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");

        if (!File.Exists(manifest))
            return [steamRoot];

        string contents;

        try
        {
            contents = File.ReadAllText(manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [steamRoot];
        }

        var paths = LibraryPathPattern.Matches(contents)
            .Select(m => m.Groups["path"].Value.Replace(@"\\", @"\", StringComparison.Ordinal))
            .ToList();

        return paths.Count > 0 ? paths : [steamRoot];
    }

    public static string? LocateSteam()
    {
        foreach (var library in ReadSteamRoot() is { } root ? FindSteamLibraries(root) : [])
        {
            var candidate = Path.Combine(library, "steamapps", "common", GameFolderName);

            if (IsValidInstall(candidate))
                return candidate;
        }

        return null;
    }

    public static IReadOnlyList<string> DriveRoots()
    {
        try
        {
            return
            [
                .. DriveInfo.GetDrives()
                    .Where(drive => drive is { IsReady: true, DriveType: DriveType.Fixed })
                    .Select(drive => drive.RootDirectory.FullName)
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // A Game Pass install that BEM cannot even list is a different answer from one that is not there,
    // so the folders that refused to be read are returned rather than counted as an absence.
    public static InstallSearch FindGamePassInstall(IEnumerable<string> driveRoots)
    {
        ArgumentNullException.ThrowIfNull(driveRoots);

        var unreadable = new List<string>();

        foreach (var root in driveRoots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            foreach (var container in GamePassContainerFolders.Select(folder => Path.Combine(root, folder)))
            {
                if (!SafeDirectoryExists(container))
                    continue;

                string[] children;

                try
                {
                    children = Directory.GetDirectories(container);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    unreadable.Add(container);
                    continue;
                }

                foreach (var child in children.OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase))
                {
                    if (IsValidInstall(child))
                        return new InstallSearch(child, unreadable);

                    var content = Path.Combine(child, GamePassContentFolderName);

                    if (IsValidInstall(content))
                        return new InstallSearch(content, unreadable);
                }
            }
        }

        return new InstallSearch(null, unreadable);
    }

    public static InstallSearch Search()
    {
        if (LocateSteam() is { } steam)
            return new InstallSearch(steam, []);

        if (LocateGogDefault() is { } gog)
            return new InstallSearch(gog, []);

        if (LocateEpic() is { } epic)
            return new InstallSearch(epic, []);

        return FindGamePassInstall(DriveRoots());
    }

    // The Epic launcher writes one small JSON manifest per installed game into ProgramData, each
    // carrying its InstallLocation. Every location is tested against the game's own folder shape
    // rather than matched by name, so a renamed listing or a localized title cannot miss and no
    // other game can false-positive: IsValidInstall is the judge either way.
    public static string? LocateEpic() =>
        LocateEpic(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests"));

    public static string? LocateEpic(string manifestsFolder)
    {
        if (!SafeDirectoryExists(manifestsFolder))
            return null;

        try
        {
            foreach (var manifest in Directory.EnumerateFiles(manifestsFolder, "*.item"))
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));

                    if (document.RootElement.TryGetProperty("InstallLocation", out var location)
                        && location.GetString() is { Length: > 0 } path
                        && IsValidInstall(path))
                    {
                        return path;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    // One unreadable manifest must not hide the rest.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    // GOG Galaxy keeps its install locations in the registry per game id, but the overwhelmingly
    // common case is the installer's default, and checking it costs two Directory.Exists calls per
    // fixed drive. Checked before the Game Pass walk because it is far cheaper than enumerating
    // WindowsApps, and a hit here saves that walk entirely.
    private static string? LocateGogDefault()
    {
        foreach (var root in DriveRoots())
        {
            foreach (var candidate in new[]
            {
                Path.Combine(root, "GOG Games", GameFolderName),
                Path.Combine(root, "Program Files (x86)", "GOG Galaxy", "Games", GameFolderName)
            })
            {
                if (IsValidInstall(candidate))
                    return candidate;
            }
        }

        return null;
    }

    public static string? Locate() => Search().Path;

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Public because the instance system needs Steam's own folder, not just the game inside it: the
    // client stages a console download under <Steam>\steamapps\content.
    public static string? ReadSteamRoot()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam", "SteamPath", null) as string
                ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
