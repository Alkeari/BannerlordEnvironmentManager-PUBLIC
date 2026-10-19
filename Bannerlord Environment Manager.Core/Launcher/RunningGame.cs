using System.Diagnostics;

namespace BannerlordEnvironmentManager.Core.Launcher;

// The second opinion LaunchCompletion polls: is anything that could be this run still on the machine.
// Read by name rather than through the Process object the launch holds, because the whole point is to
// not trust that object when it has stopped answering.
public static class RunningGame
{
    // The game itself under both names it starts under. A launch through BLSE puts the shim in the
    // process list too, and it is the one the launch holds a handle on.
    public static readonly IReadOnlyList<string> KnownProcessNames =
        ["Bannerlord", "Bannerlord.BLSE.Standalone", "Bannerlord.BLSE.LauncherEx", "Bannerlord.BLSE.Launcher", "TaleWorlds.MountAndBlade.Launcher"];

    // Every name a Bannerlord install can appear under, which is the launch-poll list plus the native
    // executable a player can start directly. Wider than KnownProcessNames on purpose: a launch BEM
    // performed can only be holding one of those, but a question like "may this instance be deleted"
    // has to see a game nothing here started. BLSE hosts the game in its own process and the TaleWorlds
    // launcher rewrites LauncherData.xml when it closes, so watching for "Bannerlord" alone misses the
    // common launch paths entirely.
    public static readonly IReadOnlyList<string> GameProcessNames =
        ["Bannerlord", "Bannerlord.Native", "Bannerlord.BLSE.Standalone", "Bannerlord.BLSE.Launcher", "Bannerlord.BLSE.LauncherEx", "TaleWorlds.MountAndBlade.Launcher"];

    // Matching on name alone can see another install's game, and that is the safe direction: a false
    // "still running" only keeps the launch waiting on the handle, which is where it was already.
    public static bool AnyRunning(IEnumerable<string>? processNames = null)
    {
        foreach (var name in processNames ?? KnownProcessNames)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var found = Process.GetProcessesByName(name);

            try
            {
                if (found.Length > 0)
                    return true;
            }
            finally
            {
                foreach (var process in found)
                    process.Dispose();
            }
        }

        return false;
    }

    // Is the game up at all, under any of its names. The answer every screen wants before it moves,
    // renames or deletes something the running game has open.
    public static bool AnyGameProcessRunning() => AnyRunning(GameProcessNames);

    public static IReadOnlyList<string> NamesFor(string? launchedExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(launchedExecutablePath))
            return KnownProcessNames;

        var started = Path.GetFileNameWithoutExtension(launchedExecutablePath);

        return string.IsNullOrEmpty(started) || KnownProcessNames.Contains(started, StringComparer.OrdinalIgnoreCase)
            ? KnownProcessNames
            : [.. KnownProcessNames, started];
    }
}
