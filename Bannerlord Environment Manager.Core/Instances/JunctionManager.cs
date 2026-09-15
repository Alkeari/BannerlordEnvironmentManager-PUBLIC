using System.Runtime.InteropServices;
using System.Text;

namespace BannerlordEnvironmentManager.Core.Instances;

// Directory junctions, not symbolic links: a junction needs no administrator rights and no developer
// mode, and BEM has to work on an ordinary user account. .NET can read and delete them but cannot
// create one, so creation goes through the reparse point ioctl by hand.
public static class JunctionManager
{
    private const int FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const int GenericWrite = 0x40000000;
    private const int OpenExisting = 3;
    private const int FileFlagBackupSemantics = 0x02000000;
    private const int FileFlagOpenReparsePoint = 0x00200000;
    private const string NonInterpretedPrefix = @"\??\";

    public static void Create(string linkPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        if (Directory.Exists(linkPath) || File.Exists(linkPath))
            throw new IOException($"'{linkPath}' already exists, so no junction was created there.");

        Directory.CreateDirectory(Path.GetFullPath(targetPath));
        Directory.CreateDirectory(linkPath);

        try
        {
            SetReparsePoint(linkPath, Path.GetFullPath(targetPath));
        }
        catch
        {
            Directory.Delete(linkPath, recursive: false);
            throw;
        }
    }

    // The reparse point attribute alone is not enough. OneDrive placeholders, deduplication stubs and a
    // few backup products set it on folders holding the user's real files, and a false positive here
    // routes Apply into the branch that deletes the link instead of the one that moves the data out. A
    // folder .NET can resolve a link target for is a link; anything else is an ordinary folder.
    public static bool IsJunction(string path)
    {
        try
        {
            return Directory.Exists(path)
                && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
                && Directory.ResolveLinkTarget(path, returnFinalTarget: false) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return false;
        }
    }

    public static string? TargetOf(string path)
    {
        try
        {
            return IsJunction(path)
                ? Directory.ResolveLinkTarget(path, returnFinalTarget: false)?.FullName
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return null;
        }
    }

    // recursive: false is deliberate and load bearing. It removes the link and never walks into the
    // target, which is where the user's saves live.
    public static void Remove(string path)
    {
        if (IsJunction(path))
            Directory.Delete(path, recursive: false);
    }

    private static void SetReparsePoint(string linkPath, string targetPath)
    {
        using var handle = CreateFile(
            linkPath, GenericWrite, 0, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);

        if (handle.IsInvalid)
            throw new IOException($"Could not open '{linkPath}' to write a junction.", Marshal.GetLastWin32Error());

        var substituteName = NonInterpretedPrefix + targetPath;
        var substituteBytes = Encoding.Unicode.GetBytes(substituteName);
        var printBytes = Encoding.Unicode.GetBytes(targetPath);

        // REPARSE_DATA_BUFFER: tag, data length, reserved, then the mount point header of four
        // ushorts followed by both names, each null terminated.
        var pathBufferLength = substituteBytes.Length + 2 + printBytes.Length + 2;
        var dataLength = 8 + pathBufferLength;
        var buffer = new byte[8 + dataLength];

        BitConverter.TryWriteBytes(buffer.AsSpan(0), IoReparseTagMountPoint);
        BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)dataLength);
        BitConverter.TryWriteBytes(buffer.AsSpan(6), (ushort)0);
        BitConverter.TryWriteBytes(buffer.AsSpan(8), (ushort)0);
        BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)substituteBytes.Length);
        BitConverter.TryWriteBytes(buffer.AsSpan(12), (ushort)(substituteBytes.Length + 2));
        BitConverter.TryWriteBytes(buffer.AsSpan(14), (ushort)printBytes.Length);
        substituteBytes.CopyTo(buffer, 16);
        printBytes.CopyTo(buffer, 16 + substituteBytes.Length + 2);

        if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException($"Could not make '{linkPath}' a junction to '{targetPath}'.", Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string fileName, int desiredAccess, int shareMode, IntPtr securityAttributes,
        int creationDisposition, int flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle device, int controlCode,
        byte[] inBuffer, int inBufferSize, IntPtr outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);
}
