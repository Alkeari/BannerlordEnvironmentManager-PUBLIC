using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Instances;

// The exclusion every part of this feature depends on, in one place because two different types need
// it: a launch holds it for its whole length, and startup repair takes it before putting a canonical
// path back, since nothing holds a handle on a junction itself and a repair would happily pull one out
// from under a running game.
//
// The key is the four canonical paths, not the games root. Two BEM windows opened on different games
// roots still move the same Documents folder, so a lock keyed on the root would let both of them
// proceed and break exactly the isolation it exists to protect.
public sealed class ActivationLock : IDisposable
{
    public const string OwnFolderName = "Bannerlord Environment Manager";

    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    // Which lock paths this process holds right now. The file handle alone cannot answer it: a
    // sharing violation looks the same whether the holder is another BEM window or this one's own
    // teardown still running past its budget, and telling the user to close a window that is not open
    // is worse than saying nothing.
    private static readonly ConcurrentDictionary<string, byte> HeldHere = new(StringComparer.OrdinalIgnoreCase);

    private readonly FileStream handle;
    private int released;

    private ActivationLock(string filePath, FileStream handle)
    {
        FilePath = filePath;
        this.handle = handle;
    }

    public string FilePath { get; }

    // True while this process is inside an activation for these paths, teardown included. A launch
    // offered while this is true is refused by the lock, so the control that starts one is held rather
    // than pressed into a refusal.
    public static bool IsHeldByThisProcess(CanonicalPathSet paths) => HeldHere.ContainsKey(PathFor(paths));

    // BEM's own folder beside the canonical AppData path, which is %LOCALAPPDATA% on a real machine and
    // the temporary root under CanonicalPathSet.Rooted. The lock is transient state rather than user
    // data: it is the one thing besides settings.json that folder holds, and it exists only while a
    // launch or a repair is running.
    public static string FolderFor(CanonicalPathSet paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var appData = Path.GetFullPath(paths.AppDataLocal);

        return Path.Combine(Path.GetDirectoryName(appData) ?? appData, OwnFolderName);
    }

    public static string PathFor(CanonicalPathSet paths) =>
        Path.Combine(FolderFor(paths), $".activation-{KeyFor(paths)}.lock");

    // A file rather than a mutex because the launch is awaited, and a mutex belongs to the thread that
    // took it while the continuation after an await is rarely that thread. An open handle dies with the
    // process, so a BEM that is killed mid-launch leaves no stale lock for the next start to puzzle out.
    public static ActivationLock Acquire(CanonicalPathSet paths)
    {
        var path = PathFor(paths);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            // FileMode.Create rather than CreateNew: a hard power loss leaves the file on disk with no
            // handle on it, and a mode that refused an existing file would block every later run.
            var handle = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            HeldHere[path] = 0;

            return new ActivationLock(path, handle);
        }
        catch (IOException ex) when (IsHeldElsewhere(ex) && HeldHere.ContainsKey(path))
        {
            throw new IOException(Strings.Current["Core.Instances.ActivationLock.HeldHere"], ex);
        }
        catch (IOException ex) when (IsHeldElsewhere(ex))
        {
            throw new IOException(Strings.Current.Format("Core.Instances.ActivationLock.HeldElsewhere", path), ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not contention. Saying it was would send the user hunting for a window that is not open
            // while the real cause, a read-only folder or antivirus holding the path, went unnamed.
            throw new IOException(
                Strings.Current.Format("Core.Instances.ActivationLock.Unavailable", path, ex.Message),
                ex);
        }
    }

    // The entry is dropped before the handle, never after: the next acquire cannot succeed until the
    // handle is gone, so dropping the entry afterwards could erase the record of whoever took it next.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) == 1)
            return;

        HeldHere.TryRemove(FilePath, out _);
        handle.Dispose();
    }

    private static bool IsHeldElsewhere(IOException ex) =>
        (ex.HResult & 0xFFFF) is SharingViolation or LockViolation;

    // The paths are normalized before hashing so that two windows naming the same folder differently,
    // a trailing separator or a different case, still land on the same lock file.
    private static string KeyFor(CanonicalPathSet paths)
    {
        var normalized = paths.Pairs
            .Select(pair => Path.TrimEndingDirectorySeparator(Path.GetFullPath(pair.LivePath)).ToLowerInvariant());

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', normalized)));

        return Convert.ToHexStringLower(digest.AsSpan(0, 8));
    }
}
