using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace BannerlordEnvironmentManager.Companion;

// Shows the Bannerlord Exception Manager's window, which is BEM itself: the Companion draws nothing in the
// game and carries no UI framework. It starts BEM on the crash record and waits for the answer BEM writes
// beside it. Every failure is no answer, and no answer leaves the crash guard's own policy in charge.
internal static class CrashGuardWindow
{
    private const int PollMilliseconds = 100;

    public static ExceptionAnswer Ask(string bemPath, string recordPath, bool wait)
    {
        try
        {
            if (string.IsNullOrEmpty(bemPath) || !File.Exists(bemPath) || !File.Exists(recordPath))
                return ExceptionAnswer.None;

            var decisionPath = ExceptionProtocol.DecisionPathFor(recordPath);

            // An answer left by an earlier run under the same name must not answer this crash.
            if (File.Exists(decisionPath))
                File.Delete(decisionPath);

            var start = new ProcessStartInfo(bemPath, "--exception \"" + recordPath + "\"")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(bemPath) ?? string.Empty
            };

            using (var process = Process.Start(start))
            {
                GrantForeground(process);

                if (process is null || !wait)
                    return ExceptionAnswer.None;

                // Bounded by the window's own life: the wait ends when the player answers or BEM is gone.
                while (true)
                {
                    var exited = process.HasExited;
                    var answer = Read(decisionPath);

                    if (answer != ExceptionAnswer.None || exited)
                        return answer;

                    Thread.Sleep(PollMilliseconds);
                }
            }
        }
        catch
        {
            return ExceptionAnswer.None;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private static void GrantForeground(Process? process)
    {
        try
        {
            if (process is not null)
                AllowSetForegroundWindow(process.Id);
        }
        catch
        {
        }
    }

    private static ExceptionAnswer Read(string decisionPath)
    {
        try
        {
            return File.Exists(decisionPath)
                ? ExceptionProtocol.ReadDecision(File.ReadAllText(decisionPath))
                : ExceptionAnswer.None;
        }
        catch
        {
            return ExceptionAnswer.None;
        }
    }
}
