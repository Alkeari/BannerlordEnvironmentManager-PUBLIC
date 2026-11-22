using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BannerlordEnvironmentManager.Core.DryRun;

// A memory dump of the game while it holds the failing frame, for the Bannerlord Exception Manager.
// Written from BEM's process by Windows' own dump writer: nothing is asked of the game, which is waiting
// on the answer, and nothing is loaded into it.
public static class GameMemoryDump
{
    public const string Extension = ".dmp";

    // The light flag set: HandleData (0x4), UnloadedModules (0x20), IndirectlyReferencedMemory (0x40) and
    // ThreadInfo (0x1000). Threads with their stacks, the memory those stacks point at, loaded and unloaded
    // modules and open handles; not the whole of the game's memory, which runs to gigabytes.
    private const uint DumpKind = 0x00000004 | 0x00000020 | 0x00000040 | 0x00001000;

    // Beside the crash record and the report, under the same name, so the dump of a crash is found with
    // the crash: <record>.json becomes <record>.dmp inside the crash-guard folder the record lives in.
    public static string PathFor(string recordPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordPath);

        return Path.ChangeExtension(recordPath, Extension);
    }

    // The size of the file written, in bytes. Throws with Windows' own reason when the game has gone or
    // the dump could not be written, and leaves no partial file behind.
    public static long Write(int processId, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var process = System.Diagnostics.Process.GetProcessById(processId);

        try
        {
            using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                if (!MiniDumpWriteDump(process.SafeHandle, (uint)processId, file.SafeFileHandle, DumpKind, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return new FileInfo(path).Length;
        }
        catch
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

#pragma warning disable SYSLIB1054 // Core does not allow unsafe blocks, which LibraryImport needs.
    [DllImport("dbghelp.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(
        SafeProcessHandle process, uint processId, SafeFileHandle file, uint kind, IntPtr exception, IntPtr userStream, IntPtr callback);
#pragma warning restore SYSLIB1054
}
