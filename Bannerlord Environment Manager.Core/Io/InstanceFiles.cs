using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BannerlordEnvironmentManager.Core.Io;

// The one place a game instance's files are linked and unlinked. A new instance hard-links every
// unchanged game file to its source when both sit on one NTFS volume (CreateHardLink) and copies
// across volumes or when a link cannot be made. A linked file shares its bytes with every other
// instance holding it, so anything BEM or a tool it starts writes into an instance's game folder
// calls BreakLink on the file first (or BreakAllLinks on a folder a tool fills): the file is copied to
// a private file beside it and moved over the name, which leaves every other instance's bytes as they were.
public static class InstanceFiles
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    // Whether the two paths sit on the same volume, the precondition for a hard link.
    public static bool SameVolume(string left, string right) =>
        string.Equals(
            Path.GetPathRoot(Path.GetFullPath(left)), Path.GetPathRoot(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    // Links source to destination when it can, otherwise copies. Returns true when a link was made.
    // The destination must not exist.
    public static bool LinkOrCopy(string source, string destination, bool allowLink = true)
    {
        if (allowLink && SameVolume(source, destination) && TryHardLink(source, destination))
            return true;

        File.Copy(source, destination, overwrite: false);
        return false;
    }

    public static bool TryHardLink(string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            return CreateHardLinkW(destination, source, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    // How many names the file's bytes have; 1 for an unlinked file or one that cannot be read.
    public static int LinkCount(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
            return 1;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            return GetFileInformationByHandle(stream.SafeFileHandle, out var info) ? (int)Math.Max(1, info.NumberOfLinks) : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 1;
        }
    }

    // Makes the file at path private to this instance before anything writes into it. A file with one
    // name, or none, is left alone. Returns true when a link was broken.
    public static bool BreakLink(string path)
    {
        if (LinkCount(path) <= 1)
            return false;

        var attributes = File.GetAttributes(path);
        var temporary = path + ".bem-private";
        try
        {
            File.Copy(path, temporary, overwrite: true);

            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // A failed copy or move must not leave a full-size stray file in the folder.
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }

        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes);

        return true;
    }

    // For a folder an outside tool (DepotDownloader) writes into: every linked file under it is made
    // private first. Returns the number broken.
    public static int BreakAllLinks(string folder)
    {
        if (!Directory.Exists(folder))
            return 0;

        var broken = 0;

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            if (BreakLink(file))
                broken++;

        return broken;
    }

    // The same pass on a pool thread. Breaking a link copies the whole file, and an instance's Game
    // folder is tens of gigabytes of linked files, so on a dispatcher-bound caller this froze the UI
    // for 217 seconds (the 2026-11-20 17:15 hang). Awaiting it keeps the result identical and the
    // caller's thread free.
    public static Task<int> BreakAllLinksAsync(string folder, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                // One pass per folder at a time: with the UI free, a second run (a DLC after the base
                // game) can start while the first is still copying, and two passes on one file collide.
                lock (PassLocks.GetOrAdd(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant(), _ => new object()))
                    return BreakAllLinksCore(folder, cancellationToken);
            },
            cancellationToken);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> PassLocks = new();

    private static int BreakAllLinksCore(string folder, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder))
            return 0;

        var broken = 0;

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (BreakLink(file))
                broken++;
        }

        return broken;
    }

    // The writes BEM makes itself, each breaking the link first.
    public static void Copy(string source, string destination, bool overwrite = true)
    {
        BreakLink(destination);
        File.Copy(source, destination, overwrite);
        MarkOfTheWeb.RemoveFromCode(destination);
    }

    public static void WriteAllText(string path, string contents)
    {
        BreakLink(path);
        File.WriteAllText(path, contents);
    }

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        BreakLink(path);
        File.WriteAllBytes(path, bytes);
        MarkOfTheWeb.RemoveFromCode(path);
    }
}
