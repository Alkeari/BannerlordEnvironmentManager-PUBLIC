using System.Diagnostics;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Launcher;

public static class GameLauncher
{
    // The started process is handed back rather than disposed here, because how a run ended is only
    // knowable to whoever still holds it. It is null for the Steam URI, which ShellExecute resolves
    // through a handler BEM never gets a handle on, and the caller owns disposing whatever it gets.
    public static Process? Launch(LaunchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // The game inherits this process's environment, and the Companion inside it reads and writes
        // BEM's files by a path of its own. A run on a separate data root says so here, or the Companion
        // would look in the owner's real folder for a session this BEM wrote somewhere else.
        Environment.SetEnvironmentVariable(DataRootVariable, DataRootForTheGame(AppDataRoot.Folder, DefaultDataRoot));

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

    // A contract with DryRunFiles in the Companion.
    public const string DataRootVariable = "BEM_DATA_ROOT";

    public static string DefaultDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        InstanceStateFolder.AppFolderName);

    // Null clears the variable: the real folder is the Companion's own default and is never named.
    public static string? DataRootForTheGame(string folder, string defaultFolder) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultFolder)),
            StringComparison.OrdinalIgnoreCase)
            ? null
            : Path.GetFullPath(folder);
}
