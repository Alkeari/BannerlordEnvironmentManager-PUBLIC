namespace BannerlordEnvironmentManager.Core.Diagnostics;

// Zero dependency: BEM decompiles on its own and dotPeek is only ever an extra. Nothing here downloads,
// installs or prompts, and an install that is not present is simply not offered.
public static class DotPeekLocator
{
    private static readonly string[] Executables = ["dotPeek64.exe", "dotPeek32.exe", "dotPeek.exe"];

    public static string? Locate() =>
        Locate(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JetBrains",
            "Installations"));

    public static string? Locate(string installationsRoot)
    {
        if (string.IsNullOrWhiteSpace(installationsRoot) || !Directory.Exists(installationsRoot))
            return null;

        try
        {
            // Toolbox writes dotPeek231, dotPeek252 and so on, so the newest by name is the newest build.
            var folders = Directory
                .EnumerateDirectories(installationsRoot, "dotPeek*")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase);

            return folders
                .SelectMany(folder => Executables.Select(exe => Path.Combine(folder, exe)))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
