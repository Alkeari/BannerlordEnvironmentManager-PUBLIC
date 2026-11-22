using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Instances;
using System.Diagnostics;

namespace BannerlordEnvironmentManager.Services
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error,
        Critical
    }

    public static class LoggingService
    {
        private static string LogFolder => Path.Combine(AppDataRoot.Folder, "Logs");

        private static string LogFile => Path.Combine(LogFolder, $"log_{DateTime.Now:yyyyMMdd}.txt");

        // Startup alone logs from the UI thread and a background archive-folder watcher within
        // milliseconds of each other. AppendAllText opens the file with no sharing, so the second of two
        // near-simultaneous calls used to throw, land in the catch below, and vanish from the file with
        // nothing to say a line was ever attempted - which looked identical to the line never having been
        // logged at all.
        private static readonly object WriteLock = new();

        private static readonly BackgroundWriter Writer = StartWriter();

        private static BackgroundWriter StartWriter()
        {
            var writer = new BackgroundWriter("BEM log writer", ex => Debug.WriteLine($"Failed to write to log file: {ex.Message}"));
            AppDomain.CurrentDomain.ProcessExit += (_, _) => writer.Flush(TimeSpan.FromSeconds(3));
            return writer;
        }

        static LoggingService()
        {
            try
            {
                Directory.CreateDirectory(LogFolder);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to create log directory: {ex.Message}");
            }
        }

        public static void Log(string message, LogLevel level = LogLevel.Info)
        {
            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
            Debug.WriteLine(logEntry);
            
            // The disk write goes to a thread of its own: a log line from the UI thread was caught
            // inside File.AppendAllText in a hang dump. The file is chosen when the line is written.
            Writer.Enqueue(() =>
            {
                lock (WriteLock)
                {
                    // Every time, not only in the static constructor: a run on a data root of its own
                    // (AppDataRoot) is pointed there after the first line was written, and its Logs
                    // folder did not exist, so every later line of a driven run was thrown away.
                    Directory.CreateDirectory(LogFolder);
                    File.AppendAllText(LogFile, logEntry + Environment.NewLine);
                }
            });

            // A critical line is written by a handler the process may not survive (an unhandled
            // exception on a background thread ends it without ProcessExit), so it waits for the disk.
            if (level == LogLevel.Critical)
                Flush();
        }

        // Waits for queued lines to reach the file, for the moments just before the process ends.
        public static void Flush() => Writer.Flush(TimeSpan.FromSeconds(3));

        public static void LogException(Exception ex, string context)
        {
            Log($"{context}: {ex.Message}{Environment.NewLine}{ex.StackTrace}", LogLevel.Error);
        }

        public static string GetLogDirectory()
        {
            return LogFolder;
        }

        public static async Task OpenLogFolderAsync()
        {
            try
            {
                if (Directory.Exists(LogFolder))
                {
#if WINDOWS
                    await Windows.System.Launcher.LaunchFolderPathAsync(LogFolder);
#else
                    // Fallback for non-Windows platforms
                    Log("Opening log folder not supported on this platform", LogLevel.Warn);
#endif
                }
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to open log folder");
            }
        }
    }
}
