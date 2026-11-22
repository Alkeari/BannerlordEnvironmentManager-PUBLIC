namespace BannerlordEnvironmentManager.Core.Game;

// Which install a screen reads when it was not handed one. The active instance wins, because that is
// the version the user picked and the one every other screen is describing; the configured path is
// only a fallback, and locating one on disk is the last resort.
//
// Order matters more than it looks. The configured path is written by the Play page's refresh, so it
// is empty until that page has run once in this session, and a screen reached without visiting Play
// fell through to the locator and read the Steam install instead. Install Checks did exactly that: it
// scanned the resting install, found none of the seventeen shadowed assemblies sitting in the instance
// the user had selected, and reported a clean install while the game it described would not start.
public static class InstallToInspect
{
    public static string Resolve(string? activeInstanceGameFolder, string? configured, Func<string?> locate)
    {
        ArgumentNullException.ThrowIfNull(locate);

        if (!string.IsNullOrWhiteSpace(activeInstanceGameFolder))
            return activeInstanceGameFolder;

        return string.IsNullOrWhiteSpace(configured) ? locate() ?? string.Empty : configured;
    }
}
