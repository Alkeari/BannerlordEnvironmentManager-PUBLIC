namespace BannerlordEnvironmentManager.Core.Toolkit;

// winget is how the Toolkit installs anything, and on this machine it is not on PATH: the app
// execution alias under WindowsApps is absent and the real executable sits in the MSIX package
// folder. Verified on a real machine, where two DesktopAppInstaller folders carry winget.exe and
// two "neutral" framework folders of a HIGHER version number do not, so a folder is only a candidate
// once the executable in it is confirmed to exist.
public static class WinGetLocator
{
    private const string PackagePrefix = "Microsoft.DesktopAppInstaller_";

    public static string? Locate() => Locate(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps"));

    public static string? Locate(string? aliasFolder, string? windowsAppsRoot)
    {
        if (!string.IsNullOrWhiteSpace(aliasFolder))
        {
            var alias = Combine(aliasFolder, "winget.exe");

            if (alias is not null && Exists(alias))
                return alias;
        }

        if (string.IsNullOrWhiteSpace(windowsAppsRoot))
            return null;

        try
        {
            return Directory
                .EnumerateDirectories(windowsAppsRoot, PackagePrefix + "*")
                .Select(folder => (Folder: folder, Version: VersionOf(folder)))
                .OrderByDescending(candidate => candidate.Version)
                .Select(candidate => Combine(candidate.Folder, "winget.exe"))
                .FirstOrDefault(path => path is not null && Exists(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // Microsoft.DesktopAppInstaller_1.30.99.0_x64__8wekyb3d8bbwe. Ordinal ordering would put 1.9 above
    // 1.30, so the middle segment is read as the version it is.
    private static Version VersionOf(string folder)
    {
        var name = Path.GetFileName(folder);
        var parts = name.Split('_');

        return parts.Length > 1 && Version.TryParse(parts[1], out var version)
            ? version
            : new Version(0, 0);
    }

    private static string? Combine(string root, string name)
    {
        try
        {
            return Path.Combine(root, name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
