using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BannerlordEnvironmentManager.Companion;

// A fault inside the engine's native code (an access violation in TaleWorlds.Native.dll, as one of his
// v1.5.7 sessions died on) never becomes a .NET exception, so neither the tick finalizers nor
// AppDomain.UnhandledException ever see it and the session left no record at all. Windows' last-chance
// filter does see it: this one writes the crash record naming the fault code and the module the
// faulting address sits in, then hands the fault on to whatever filter was there before (the engine's
// own dump writer), so the game ends exactly as it would have.
internal static class NativeFaultGuard
{
    private const int ExceptionContinueSearch = 0;
    private const uint GetModuleHandleExFlagFromAddress = 0x4;
    private const uint GetModuleHandleExFlagUnchangedRefcount = 0x2;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int TopLevelFilter(IntPtr exceptionPointers);

    // Held for the whole process: the native side keeps only the pointer.
    private static TopLevelFilter? _filter;
    private static IntPtr _previous;
    private static Action<string, string>? _record;
    private static int _fired;
    private static int _inside;
    private static IntPtr _vectored;
    private static string _lastFatal = string.Empty;
    private static long _lastFatalTicks;

    // A fault a vectored handler saw first and nothing since has shown to be handled: read by the
    // process-exit hook, which records it when the game ends abnormally within seconds of it.
    public static string LastFatalWithin(TimeSpan window) =>
        _lastFatal.Length > 0 && System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000) - System.Threading.Interlocked.Read(ref _lastFatalTicks) <= window.TotalMilliseconds
            ? _lastFatal
            : string.Empty;

    public static void Install(Action<string, string> record)
    {
        try
        {
            _record = record;
            _filter ??= OnFault;
            var mine = Marshal.GetFunctionPointerForDelegate(_filter);
            var previous = SetUnhandledExceptionFilter(mine);

            // Installed again later to sit above a filter the engine set after us; never chain to ourselves.
            if (previous != mine)
                _previous = previous;
        }
        catch
        {
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int VectoredHandler(IntPtr exceptionPointers);

    private static VectoredHandler? _vectoredHandler;

    // Runs before any frame-based handler, the engine's own included. It only remembers fatal codes
    // raised inside native modules (a managed null dereference is a first-chance fault the runtime
    // handles), so a benign probe costs nothing and the real one is on record when the process ends.
    public static void InstallVectored()
    {
        try
        {
            if (_vectored != IntPtr.Zero)
                return;

            _vectoredHandler = OnVectored;
            _vectored = AddVectoredExceptionHandler(1, Marshal.GetFunctionPointerForDelegate(_vectoredHandler));
        }
        catch
        {
        }
    }

    private static int OnVectored(IntPtr exceptionPointers)
    {
        try
        {
            var recordPtr = Marshal.ReadIntPtr(exceptionPointers);
            var code = unchecked((uint)Marshal.ReadInt32(recordPtr));

            if (code == 0xC0000005 || code == 0xC00000FD || code == 0xC000001D || code == 0xC0000374)
            {
                var address = Marshal.ReadIntPtr(recordPtr, 8 + IntPtr.Size);
                var module = ModuleOf(address);

                if (module != "an unknown module" && !module.StartsWith("coreclr", StringComparison.OrdinalIgnoreCase)
                    && !module.StartsWith("clr", StringComparison.OrdinalIgnoreCase))
                {
                    _lastFatal = $"0x{code:X8} at 0x{address.ToInt64():X} in {module} on thread {GetCurrentThreadId()}";
                    System.Threading.Interlocked.Exchange(ref _lastFatalTicks, System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000));
                }
            }
        }
        catch
        {
        }

        return ExceptionContinueSearch;
    }

    private static int OnFault(IntPtr exceptionPointers)
    {
        // Retaking the top can leave the engine's filter holding ours as its previous; when it chains back,
        // this call ends here instead of looping between the two.
        if (System.Threading.Interlocked.Exchange(ref _inside, 1) == 1)
            return ExceptionContinueSearch;

        try
        {
            if (System.Threading.Interlocked.Exchange(ref _fired, 1) == 0 && exceptionPointers != IntPtr.Zero)
            {
                var recordPtr = Marshal.ReadIntPtr(exceptionPointers);
                var code = unchecked((uint)Marshal.ReadInt32(recordPtr));
                // EXCEPTION_RECORD: Code, Flags (4 + 4), then a pointer to the next record, then the address.
                var address = Marshal.ReadIntPtr(recordPtr, 8 + IntPtr.Size);
                var module = ModuleOf(address);
                var type = code switch
                {
                    0xC0000005 => "NativeAccessViolation",
                    0xC00000FD => "NativeStackOverflow",
                    0xE0434352 => "UncaughtManagedException",
                    _ => "NativeFault",
                };
                _record?.Invoke(type, $"0x{code:X8} at 0x{address.ToInt64():X} in {module} on thread {GetCurrentThreadId()}");
            }
        }
        catch
        {
        }

        if (_previous != IntPtr.Zero)
        {
            try
            {
                return Marshal.GetDelegateForFunctionPointer<TopLevelFilter>(_previous)(exceptionPointers);
            }
            catch
            {
            }
        }

        return ExceptionContinueSearch;
    }

    private static string ModuleOf(IntPtr address)
    {
        try
        {
            if (GetModuleHandleEx(GetModuleHandleExFlagFromAddress | GetModuleHandleExFlagUnchangedRefcount, address, out var handle))
            {
                var name = new StringBuilder(520);
                if (GetModuleFileName(handle, name, name.Capacity) > 0)
                    return System.IO.Path.GetFileName(name.ToString());
            }
        }
        catch
        {
        }

        return "an unknown module";
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr AddVectoredExceptionHandler(uint first, IntPtr handler);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern IntPtr SetUnhandledExceptionFilter(IntPtr filter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetModuleHandleEx(uint flags, IntPtr address, out IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileName(IntPtr module, StringBuilder name, int size);
}
