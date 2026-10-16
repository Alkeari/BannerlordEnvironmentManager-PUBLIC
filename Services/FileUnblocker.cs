using System.Diagnostics;
using System.Runtime.InteropServices;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Install;

namespace BannerlordEnvironmentManager.Services
{
    /// <summary>
    /// Service for unblocking files that have been downloaded from the internet.
    /// Files downloaded from the internet are marked with a Zone.Identifier alternate data stream.
    /// </summary>
    internal static class FileUnblocker
    {
        /// <summary>
        /// Result of an unblock operation for a single file.
        /// </summary>
        internal record UnblockResult(string FilePath, UnblockStatus Status, string? ErrorMessage = null);

        /// <summary>
        /// Status of an unblock operation.
        /// </summary>
        internal enum UnblockStatus
        {
            Unblocked,
            AlreadyUnblocked,
            Unknown,
            Failed
        }

        /// <summary>
        /// Statistics for a batch unblock operation.
        /// </summary>
        internal class UnblockStatistics
        {
            public int TotalFiles { get; set; }
            public int Unblocked { get; set; }
            public int AlreadyUnblocked { get; set; }
            public int Unknown { get; set; }
            public int Failed { get; set; }
        }

        // File.Exists answers false both for a stream that is not there and for a file the system would
        // not let us look at, and those are opposite answers: the second one must never be counted as
        // already clean. Opening the stream separates them, because only the first throws FileNotFound.
        private readonly record struct ZoneCheck(bool Blocked, string? Error);

        private static ZoneCheck CheckZoneIdentifier(string filePath)
        {
            try
            {
                using var stream = new FileStream(
                    $"{filePath}:Zone.Identifier",
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                return new ZoneCheck(true, null);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new ZoneCheck(false, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or NotSupportedException or ArgumentException)
            {
                return new ZoneCheck(false, ex.Message);
            }
        }

        /// <summary>
        /// Check if a file has the Zone.Identifier stream (is blocked). False also when the file could
        /// not be checked, so use CheckZoneIdentifier where the difference matters.
        /// </summary>
        public static bool HasZoneIdentifier(string filePath) =>
            File.Exists(filePath) && CheckZoneIdentifier(filePath).Blocked;

        /// <summary>
        /// Unblock a single file by removing the Zone.Identifier alternate data stream.
        /// </summary>
        public static UnblockResult UnblockFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                LoggingService.Log($"Unblock failed: File not found {filePath}", LogLevel.Warn);
                return new UnblockResult(filePath, UnblockStatus.Failed, Strings.Current["Unblock.Result.FileNotFound"]);
            }

            try
            {
                var zone = CheckZoneIdentifier(filePath);

                if (zone.Error is not null)
                {
                    LoggingService.Log($"Could not tell whether {filePath} is blocked: {zone.Error}", LogLevel.Warn);
                    return new UnblockResult(filePath, UnblockStatus.Unknown, zone.Error);
                }

                if (!zone.Blocked)
                {
                    return new UnblockResult(filePath, UnblockStatus.AlreadyUnblocked);
                }

                LoggingService.Log($"Unblocking file: {filePath}");
                // Use DeleteFile Win32 API to remove the alternate data stream
                var streamPath = $"{filePath}:Zone.Identifier";
                if (DeleteFile(streamPath))
                {
                    return new UnblockResult(filePath, UnblockStatus.Unblocked);
                }

                LoggingService.Log($"Win32 DeleteFile failed for {streamPath}, falling back to PowerShell", LogLevel.Warn);
                // Fallback: use PowerShell Unblock-File cmdlet
                return UnblockFilePowerShell(filePath);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, $"Exception while unblocking {filePath}");
                return new UnblockResult(filePath, UnblockStatus.Failed, ex.Message);
            }
        }

        /// <summary>
        /// Unblock all files in a directory recursively.
        /// </summary>
        public static async Task<(UnblockStatistics Stats, List<UnblockResult> Results)> UnblockDirectoryAsync(
            string directoryPath,
            IProgress<(int current, int total, string currentFile)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");
            }

            var stats = new UnblockStatistics();
            var results = new List<UnblockResult>();

            await Task.Run(() =>
            {
                LoggingService.Log($"{InstallLogArchiveLinks.DirectoryUnblocked}{directoryPath}");
                var files = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories).ToList();
                stats.TotalFiles = files.Count;
                LoggingService.Log($"Found {files.Count} files to check for unblocking in {directoryPath}");

                for (int i = 0; i < files.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var file = files[i];
                    progress?.Report((i + 1, files.Count, file));

                    var result = UnblockFile(file);
                    results.Add(result);

                    switch (result.Status)
                    {
                        case UnblockStatus.Unblocked:
                            stats.Unblocked++;
                            break;

                        case UnblockStatus.AlreadyUnblocked:
                            stats.AlreadyUnblocked++;
                            break;

                        case UnblockStatus.Unknown:
                            stats.Unknown++;
                            break;

                        case UnblockStatus.Failed:
                            stats.Failed++;
                            break;
                    }
                }
            }, cancellationToken);

            return (stats, results);
        }

        /// <summary>
        /// Unblock specific files.
        /// </summary>
        public static async Task<(UnblockStatistics Stats, List<UnblockResult> Results)> UnblockFilesAsync(
            IEnumerable<string> filePaths,
            IProgress<(int current, int total, string currentFile)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var stats = new UnblockStatistics();
            var results = new List<UnblockResult>();
            var fileList = filePaths.ToList();

            await Task.Run(() =>
            {
                stats.TotalFiles = fileList.Count;

                for (int i = 0; i < fileList.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var file = fileList[i];
                    progress?.Report((i + 1, fileList.Count, file));

                    var result = UnblockFile(file);
                    results.Add(result);

                    switch (result.Status)
                    {
                        case UnblockStatus.Unblocked:
                            stats.Unblocked++;
                            break;

                        case UnblockStatus.AlreadyUnblocked:
                            stats.AlreadyUnblocked++;
                            break;

                        case UnblockStatus.Unknown:
                            stats.Unknown++;
                            break;

                        case UnblockStatus.Failed:
                            stats.Failed++;
                            break;
                    }
                }
            }, cancellationToken);

            return (stats, results);
        }

        private static UnblockResult UnblockFilePowerShell(string filePath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"Unblock-File -LiteralPath '{filePath.Replace("'", "''")}'\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return new UnblockResult(
                        filePath, UnblockStatus.Failed, Strings.Current["Unblock.Result.PowerShellNotStarted"]);
                }

                process.WaitForExit();

                if (process.ExitCode == 0)
                {
                    return new UnblockResult(filePath, UnblockStatus.Unblocked);
                }

                var error = process.StandardError.ReadToEnd();
                return new UnblockResult(filePath, UnblockStatus.Failed, error);
            }
            catch (Exception ex)
            {
                return new UnblockResult(filePath, UnblockStatus.Failed, ex.Message);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteFile(string lpFileName);
    }
}


