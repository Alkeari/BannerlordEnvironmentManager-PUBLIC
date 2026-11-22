using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BannerlordEnvironmentManager.Services;

// One Windows job object, created the first time BEM starts a child process and never closed while
// BEM runs. Its only setting is JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: when this process ends, for any
// reason at all, Windows closes the handle and ends everything in the job with it.
//
// This exists for the exit path no code of BEM's gets to run on. A download that is stopped or that
// fails is killed by DepotDownloaderTool itself; BEM being closed, crashing, or being killed from
// Task Manager runs nothing, and a DepotDownloader left behind that way carries on writing
// gigabytes into a folder with nothing watching it.
//
// Failure anywhere here costs the last-resort exit path and nothing else: it is logged and the
// download goes on, exactly as it did before this existed.
internal static partial class ChildProcessJob
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private static readonly object Gate = new();

    private static IntPtr job;

    private static bool refused;

    public static void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        lock (Gate)
        {
            if (refused)
                return;

            if (job == IntPtr.Zero && !TryCreate())
            {
                refused = true;
                return;
            }

            if (!AssignProcessToJobObject(job, process.Handle))
            {
                LoggingService.Log(
                    "A child process could not be put in BEM's job object "
                    + $"(error {Marshal.GetLastPInvokeError()}), so closing BEM will not end it.",
                    LogLevel.Warn);
            }
        }
    }

    private static bool TryCreate()
    {
        var created = CreateJobObject(IntPtr.Zero, null);

        if (created == IntPtr.Zero)
        {
            LoggingService.Log(
                $"BEM could not create the job object its child processes go in (error {Marshal.GetLastPInvokeError()}).",
                LogLevel.Warn);

            return false;
        }

        var limits = new ExtendedLimitInformationBlock
        {
            BasicLimitInformation = new BasicLimitInformationBlock { LimitFlags = KillOnJobClose }
        };

        var size = Marshal.SizeOf<ExtendedLimitInformationBlock>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);

            if (!SetInformationJobObject(created, ExtendedLimitInformation, buffer, (uint)size))
            {
                LoggingService.Log(
                    "BEM's job object refused the kill-on-close limit "
                    + $"(error {Marshal.GetLastPInvokeError()}), so closing BEM will not end its child processes.",
                    LogLevel.Warn);

                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        // Deliberately never closed: closing the handle is what kills the job, so the handle lives
        // as long as BEM does and Windows closes it when the process ends.
        job = created;
        return true;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformationBlock
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCountersBlock
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformationBlock
    {
        public BasicLimitInformationBlock BasicLimitInformation;
        public IoCountersBlock IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
