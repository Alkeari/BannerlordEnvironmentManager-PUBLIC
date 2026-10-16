using System.Xml.Linq;

namespace BannerlordEnvironmentManager.Core.Io;

public sealed record BackupRestoreResult(int Restored, int Missing, IReadOnlyList<string> Failed);

public static class AtomicXmlFile
{
    public const string BackupExtension = ".bem.bak";

    private const string TemporaryExtension = ".bem.tmp";

    public static string BackupPathFor(string path) => path + BackupExtension;

    public static bool HasBackup(string path) => File.Exists(BackupPathFor(path));

    // The backup is moved rather than copied so that the restore consumes it: leaving it behind
    // would keep reporting a pending restore that has already happened.
    public static bool Restore(string path)
    {
        var backupPath = BackupPathFor(path);

        if (!File.Exists(backupPath))
            return false;

        File.Move(backupPath, path, overwrite: true);
        return true;
    }

    // A manifest that cannot be written must not strand the rest of the batch half restored. Each
    // restore is an independent move, so a failure leaves that manifest and its backup exactly as
    // they were and the same call can be repeated once the cause is cleared.
    public static BackupRestoreResult RestoreAll(IEnumerable<string> paths)
    {
        var restored = 0;
        var missing = 0;
        var failed = new List<string>();

        foreach (var path in paths)
        {
            try
            {
                if (Restore(path))
                    restored++;
                else
                    missing++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(path);
            }
        }

        return new BackupRestoreResult(restored, missing, failed);
    }

    public static void Save(XDocument document, string path, bool writeBackup)
    {
        ArgumentNullException.ThrowIfNull(document);

        Save(temporaryPath => document.Save(temporaryPath), path, writeBackup);
    }

    public static void Save(byte[] bytes, string path, bool writeBackup)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        Save(temporaryPath => File.WriteAllBytes(temporaryPath, bytes), path, writeBackup);
    }

    private static void Save(Action<string> writeTemporary, string path, bool writeBackup)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = path + TemporaryExtension;

        try
        {
            writeTemporary(temporaryPath);

            if (!File.Exists(path))
            {
                File.Move(temporaryPath, path);
                return;
            }

            var backupPath = BackupPathFor(path);

            if (writeBackup && !File.Exists(backupPath))
                File.Copy(path, backupPath, overwrite: false);

            File.Replace(temporaryPath, path, destinationBackupFileName: null);
        }
        catch
        {
            Delete(temporaryPath);
            throw;
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original file is already safe; a stranded temp file must not mask the real error.
        }
    }
}
