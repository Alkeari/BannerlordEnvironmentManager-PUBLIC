using System.Diagnostics;

namespace BannerlordEnvironmentManager.Core.Launcher;

public static class GameLauncher
{
    // The started process is handed back rather than disposed here, because how a run ended is only
    // knowable to whoever still holds it. It is null for the Steam URI, which ShellExecute resolves
    // through a handler BEM never gets a handle on, and the caller owns disposing whatever it gets.
    public static Process? Launch(LaunchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Required for the steam:// URI to resolve; harmless for the executable targets.
        var startInfo = new ProcessStartInfo
        {
            FileName = target.Path,
            Arguments = target.Arguments,
            UseShellExecute = true,
            WorkingDirectory = target.IsUri ? string.Empty : Path.GetDirectoryName(target.Path) ?? string.Empty
        };

        return Process.Start(startInfo);
    }
}
