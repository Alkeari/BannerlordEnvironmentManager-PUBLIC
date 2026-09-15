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
        private static readonly string LogFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bannerlord Environment Manager",
            "Logs");

        private static readonly string LogFile = Path.Combine(LogFolder, $"log_{DateTime.Now:yyyyMMdd}.txt");

        // Startup alone logs from the UI thread and a background archive-folder watcher within
        // milliseconds of each other. AppendAllText opens the file with no sharing, so the second of two
        // near-simultaneous calls used to throw, land in the catch below, and vanish from the file with
        // nothing to say a line was ever attempted - which looked identical to the line never having been
        // logged at all.
        private static readonly object WriteLock = new();

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
            
            try
            {
                lock (WriteLock)
                {
                    File.AppendAllText(LogFile, logEntry + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to write to log file: {ex.Message}");
            }
        }

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
