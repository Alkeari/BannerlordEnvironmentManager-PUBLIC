using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Toolkit;

// The one place BEM actually starts an installer. It is never called except from an explicit press,
// and it distinguishes a declined permission prompt from a failure, because those are opposite
// statements to whoever reads the result.
public static class WinGetRunner
{
    // Windows returns ERROR_CANCELLED when the elevation prompt is dismissed rather than answered.
    private const int ErrorCancelled = 1223;

    public static async Task<ProcessOutcome> RunAsync(ToolCommand command, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            Arguments = command.Arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        Process? process;

        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new ProcessOutcome(ProcessResult.RefusedByUser, -1, ex.Message);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new ProcessOutcome(ProcessResult.CouldNotStart, -1, ex.Message);
        }

        if (process is null)
        {
            return new ProcessOutcome(
                ProcessResult.CouldNotStart, -1, Strings.Current["Core.Toolkit.WinGetRunner.DidNotStart"]);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }

            var output = string.Join(
                System.Environment.NewLine,
                new[] { await stdout, await stderr }.Where(part => part.Trim().Length > 0));

            return new ProcessOutcome(ProcessResult.Completed, process.ExitCode, Readable(output));
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            _ = ex;
        }
    }

    // winget redraws its progress bar with carriage returns and box-drawing spinner glyphs, which
    // arrive as one enormous unreadable line once the output is captured rather than drawn.
    private static string Readable(string output)
    {
        var lines = output
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Any(c => !char.IsWhiteSpace(c) && c is not ('-' or '\\' or '/' or '|' or '█' or '▒')))
            .Distinct(StringComparer.Ordinal);

        return string.Join(" ", lines);
    }
}
