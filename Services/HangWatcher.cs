using System.Diagnostics;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Instances;
using Microsoft.UI.Dispatching;

namespace BannerlordEnvironmentManager.Services
{
    /// <summary>Starts the UI responsiveness watcher: thin glue over Core's <see cref="ResponsivenessWatcher"/>.</summary>
    public static class HangWatcher
    {

        private static readonly object FileLock = new();
        private static string LogsFolder => Path.Combine(AppDataRoot.Folder, "Logs");

        public static void Start(DispatcherQueue queue)
        {
            var watcher = new ResponsivenessWatcher(
                () => Environment.TickCount64,
                work => queue.TryEnqueue(() => work()),
                Append,
                WriteDump);

            var thread = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(250);
                    try { watcher.Tick(); } catch (Exception ex) { Debug.WriteLine(ex); }
                }
            }) { IsBackground = true, Name = "BEM responsiveness watcher", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        private static void Append(string line)
        {
            try
            {
                lock (FileLock)
                {
                    Directory.CreateDirectory(LogsFolder);
                    File.AppendAllText(Path.Combine(LogsFolder, $"responsiveness_{DateTime.Now:yyyyMMdd}.txt"), line + Environment.NewLine);
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        // A process cannot reliably dump itself: MiniDumpWriteDump on the current process left a 0-byte file and ended
        // BEM in the 2026-11-16 UI sweep. Dumps come from Watch-Responsiveness.ps1, which dumps BEM from outside.
        private static void WriteDump(string tag) => Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}	DUMP-SKIPPED	{tag}: hang dumps are written by Watch-Responsiveness.ps1 from outside the process");
    }
}
