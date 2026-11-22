namespace BannerlordEnvironmentManager.Core.Install;

/// <summary>
/// The files Windows refuses to load from a downloaded mod while they carry the downloaded-from-the-internet mark:
/// every .dll and .exe under each module's bin folder. Unblocking these runs before every launch.
/// </summary>
public static class PreLaunchUnblock
{
    private static readonly string[] Extensions = [".dll", ".exe"];

    public static IReadOnlyList<string> Candidates(string gameRoot)
    {
        var modules = Path.Combine(gameRoot, "Modules");
        if (!Directory.Exists(modules))
            return [];
        var found = new List<string>();
        foreach (var module in SafeDirectories(modules))
        {
            var bin = Path.Combine(module, "bin");
            if (!Directory.Exists(bin))
                continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(bin, "*", new EnumerationOptions
                         {
                             RecurseSubdirectories = true, IgnoreInaccessible = true,
                             AttributesToSkip = FileAttributes.ReparsePoint,
                         }))
                {
                    if (Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                        found.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return found;
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
