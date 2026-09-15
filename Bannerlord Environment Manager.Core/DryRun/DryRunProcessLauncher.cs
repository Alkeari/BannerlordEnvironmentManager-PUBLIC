using System.Diagnostics;

namespace BannerlordEnvironmentManager.Core.DryRun;

// The real launcher behind DryRunOrchestrator. Cancellation kills the process, which is the only
// way to stop a dry run: a mod that hangs rather than throwing never reaches the first tick, and the
// breadcrumb file still names where it stopped.
public static class DryRunProcessLauncher
{
    public static DryRunLaunch Create() => async (target, cancellationToken) =>
    {
        ArgumentNullException.ThrowIfNull(target);

        // Not UseShellExecute: a dry run has to hold the process handle so it can be waited on and
        // killed, and no console window is wanted for either the game or the wait.
        var startInfo = new ProcessStartInfo
        {
            FileName = target.Path,
            Arguments = target.Arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(target.Path) ?? string.Empty
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"'{target.Path}' did not start.");

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    };

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            // A process that has already gone, or that refuses to die, must not replace the
            // cancellation the caller asked for with an unrelated failure.
        }
    }
}
