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

    // The install a launched executable belongs to: everything above its `bin` folder, or the
    // executable's own folder when it is not under one.
    public static string InstallRootOf(string launchedExecutablePath)
    {
        var folder = Path.GetDirectoryName(launchedExecutablePath) ?? string.Empty;
        var marker = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        var at = (folder + Path.DirectorySeparatorChar).IndexOf(marker, StringComparison.OrdinalIgnoreCase);

        return at > 0 ? folder[..at] : folder;
    }

    // Like AnyRunning, but only a process whose executable sits under the given install counts. A
    // same-named game of another instance (another session's run, a leftover from an earlier one) is
    // not this run: counting it kept a finished run "alive" and its junctions standing. A process whose
    // path cannot be read still counts, the safe direction, as in AnyRunning.
    public static bool AnyRunningFrom(string installRoot, IEnumerable<string>? processNames = null)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            return AnyRunning(processNames);

        var prefix = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var name in processNames ?? KnownProcessNames)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    string? path;

                    try
                    {
                        if (process.HasExited)
                            continue;

                        path = process.MainModule?.FileName;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        path = null;
                    }

                    if (path is null || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }

    // Is the game up at all, under any of its names. The answer every screen wants before it moves,
    // renames or deletes something the running game has open.
    public static bool AnyGameProcessRunning() => AnyRunning(GameProcessNames);

    // The executable path of every running game process, null for one whose path cannot be read (an
    // elevated or other user's process), which a caller must treat as possibly inside any folder.
    public static IReadOnlyList<string?> RunningGamePaths()
    {
        var paths = new List<string?>();

        foreach (var name in GameProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    try { paths.Add(process.MainModule?.FileName); }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                    { paths.Add(null); }
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return paths;
    }

    // Does a running game hold this game folder: its executable lies inside it, or its path is unknown.
    // A game from another version's folder does not, so installing into one version while another is
    // played is allowed.
    public static bool HoldsFolder(IEnumerable<string?> runningGamePaths, string gameFolder)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameFolder)) + Path.DirectorySeparatorChar;

        return runningGamePaths.Any(path => path is null
            || Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase));
    }

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
