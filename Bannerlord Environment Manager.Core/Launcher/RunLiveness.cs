using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BannerlordEnvironmentManager.Core.Launcher;

// One row of the machine's process list. ImagePath is null when the process cannot be opened to read
// it (an elevated or other user's process, or one that ended between the list and the read).
public sealed record ProcessEntry(int Id, int ParentId, string Name, string? ImagePath);

// Is a launched run still on the machine. The handle BEM holds is not the whole answer: a launcher
// that hands off or re-execs ends its own process while the game goes on, and a handle that stops
// answering (an exit read as -1, or "no process is associated") says nothing about the game. Tearing
// the junctions down then pulls the instance's data out from under a live game. The run is alive while
// a game executable runs from the instance's Game folder, or the tracked process is still a game
// executable, or a game executable it started is alive. Only game executables count, so a stray tool
// under the folder or a reused process id cannot hold the junctions for ever.
public static class RunLiveness
{
    public static bool IsAlive(IReadOnlyList<ProcessEntry> snapshot, string? gameFolder, int? trackedProcessId = null) =>
        IsAlive(snapshot, PrefixesFor(gameFolder), trackedProcessId);

    public static bool IsAlive(IReadOnlyList<ProcessEntry> snapshot, IReadOnlyList<string> folderPrefixes, int? trackedProcessId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(folderPrefixes);

        var family = FamilyOf(snapshot, trackedProcessId);

        foreach (var entry in snapshot)
        {
            if (!LooksLikeTheGame(entry))
                continue;

            if (family.Contains(entry.Id))
                return true;

            if (entry.ImagePath is null)
            {
                // A path that cannot be read could be this folder. Without a tracked process that is
                // the safe answer; with one, an unreadable game outside its family is another run.
                if (trackedProcessId is null)
                    return true;

                continue;
            }

            if (folderPrefixes.Count == 0)
                return true;

            var image = SafeFullPath(entry.ImagePath);

            if (folderPrefixes.Any(prefix => image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    // The tracked process and everything descended from it by parent id.
    private static HashSet<int> FamilyOf(IReadOnlyList<ProcessEntry> snapshot, int? trackedProcessId)
    {
        var family = new HashSet<int>();

        if (trackedProcessId is not { } tracked)
            return family;

        family.Add(tracked);
        var grew = true;

        while (grew)
        {
            grew = false;

            foreach (var entry in snapshot)
            {
                if (entry.Id != entry.ParentId && family.Contains(entry.ParentId) && family.Add(entry.Id))
                    grew = true;
            }
        }

        return family;
    }

    // The folder as given and, when it exists, as the file system resolves it: the image path the
    // process list reports goes through junctions and 8.3 names, and a referenced Game folder is often
    // reached through one.
    public static IReadOnlyList<string> PrefixesFor(string? gameFolder)
    {
        if (string.IsNullOrWhiteSpace(gameFolder))
            return [];

        var prefixes = new List<string> { AsPrefix(SafeFullPath(gameFolder)) };

        if (FinalPathOf(gameFolder) is { } resolved && !prefixes.Contains(AsPrefix(resolved), StringComparer.OrdinalIgnoreCase))
            prefixes.Add(AsPrefix(resolved));

        return prefixes;
    }

    private static string AsPrefix(string folder) =>
        Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;

    private static bool LooksLikeTheGame(ProcessEntry entry) => IsGameName(entry.Name);

    private static bool IsGameName(string name) =>
        RunningGame.GameProcessNames.Contains(Path.GetFileNameWithoutExtension(name), StringComparer.OrdinalIgnoreCase);

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string? FinalPathOf(string folder)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(folder))
            return null;

        // FILE_SHARE_READ | WRITE | DELETE, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS to open a folder.
        var handle = CreateFileW(folder, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);

        if (handle == InvalidHandle)
            return null;

        try
        {
            var buffer = new char[32768];
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);

            if (length == 0 || length >= buffer.Length)
                return null;

            var path = new string(buffer, 0, (int)length);

            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                return @"\\" + path[8..];

            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // Polls until the run has been seen gone on two reads in a row, so a game that restarts itself
    // (the old process gone a moment before the new one appears) is not read as an end. A probe that
    // throws counts as alive: not being able to look is not evidence the game is gone.
    public static async Task WaitUntilGoneAsync(
        Func<bool> alive, TimeSpan? pollInterval = null, Action<string>? log = null, TimeSpan? heartbeat = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alive);

        var poll = pollInterval ?? LaunchCompletion.DefaultPollInterval;
        var every = heartbeat ?? LaunchCompletion.HeartbeatEvery;
        var goneReads = 0;
        var waited = TimeSpan.Zero;
        var lastNote = TimeSpan.Zero;

        while (true)
        {
            if (Probe(alive, log))
            {
                goneReads = 0;

                if (waited - lastNote >= every && waited > TimeSpan.Zero)
                {
                    lastNote = waited;
                    log?.Invoke($"The junctions stay up: a process of this run is still alive after {waited.TotalMinutes:0} minute(s).");
                }
            }
            else if (++goneReads >= 2)
            {
                return;
            }

            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            waited += poll;
        }
    }

    public static bool Probe(Func<bool> alive, Action<string>? log)
    {
        try
        {
            return alive();
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not tell whether the game is still running: {ex.Message}");
            return true;
        }
    }

    // The machine's process list with parent ids and image paths, read through the Toolhelp snapshot
    // rather than Process.MainModule, which fails across bitness and for any process it cannot open
    // with full rights.
    public static IReadOnlyList<ProcessEntry> Snapshot()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var list = new List<ProcessEntry>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);

        if (snapshot == InvalidHandle)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };

            if (!Process32FirstW(snapshot, ref entry))
                return list;

            do
            {
                var id = (int)entry.th32ProcessID;
                var name = entry.szExeFile ?? string.Empty;

                // Only a game executable is ever matched by path, so only those are opened.
                list.Add(new ProcessEntry(id, (int)entry.th32ParentProcessID, name, IsGameName(name) ? ImagePathOf(id) : null));
                entry.dwSize = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return list;
    }

    private static string? ImagePathOf(int processId)
    {
        if (processId == 0)
            return null;

        var handle = OpenProcess(QueryLimitedInformation, false, (uint)processId);

        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var buffer = new char[32768];
            var size = (uint)buffer.Length;

            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint SnapProcess = 0x00000002;
    private const uint QueryLimitedInformation = 0x1000;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

#pragma warning disable SYSLIB1054 // Core does not allow unsafe blocks, which LibraryImport needs.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] name, ref uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(IntPtr file, char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
#pragma warning restore SYSLIB1054
}
