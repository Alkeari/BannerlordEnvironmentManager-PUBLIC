using System.Globalization;
using BannerlordEnvironmentManager.Core.Localization;
using Microsoft.Diagnostics.Runtime;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public enum CrashDumpOutcome
{
    // A managed exception was found on a thread of the crashed process.
    ManagedException,

    // The dump was read and no thread carried a managed exception. A hang, a native fault or a
    // process somebody killed all land here, and none of them is a failure to read the file.
    NoManagedException,

    // The dump could not be read. This is not the same statement as the one above and never says the
    // same thing to the reader.
    Unreadable
}

// One frame of a managed stack as the dump gives it. A frame whose method the dump does not carry is
// kept rather than dropped, because "two frames here did not resolve" is the difference between a
// stack that names a mod and one that does not, and hiding them would make the stack look complete.
public sealed record CrashDumpFrame(string? Method, string? ModulePath)
{
    public bool IsResolved => !string.IsNullOrWhiteSpace(Method);

    public string Describe() => IsResolved
        ? Method!
        : Strings.Current["Core.Diagnostics.CrashDump.UnresolvedFrame"];
}

public sealed record CrashDumpReading(
    string Path,
    string ProcessName,
    DateTimeOffset Written,
    long SizeBytes,
    CrashDumpOutcome Outcome,
    string? RuntimeVersion = null,
    string? ExceptionTypeName = null,
    string? ExceptionMessage = null,
    IReadOnlyList<CrashDumpFrame>? Frames = null,
    string? Error = null)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public IReadOnlyList<CrashDumpFrame> Stack => Frames ?? [];

    public int UnresolvedFrameCount => Stack.Count(f => !f.IsResolved);

    // The modules the stack actually names, in the order the frames name them. This is evidence, not
    // attribution: a module on the stack is a module that was running, which is not the same as a
    // module that is at fault.
    public IReadOnlyList<string> NamedModulePaths =>
    [
        .. Stack
            .Select(f => f.ModulePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
    ];

    public string Headline() => Outcome switch
    {
        CrashDumpOutcome.ManagedException => $"{ExceptionTypeName}: {ExceptionMessage}",
        CrashDumpOutcome.NoManagedException => Strings.Current["Core.Diagnostics.CrashDump.NoManagedException"],
        _ => Strings.Current.Format("Core.Diagnostics.CrashDump.Unreadable", Error)
    };

    // The sentence this whole feature exists to be able to write honestly. A stack with a hole in it
    // is reported as a stack with a hole in it, and BEM names no module the stack does not name.
    public string DescribeAttribution()
    {
        if (Outcome != CrashDumpOutcome.ManagedException)
            return Strings.Current["Core.Diagnostics.CrashDump.NoAttribution"];

        if (Stack.Count == 0)
            return Strings.Current["Core.Diagnostics.CrashDump.NoStack"];

        var named = NamedModulePaths;

        var names = named.Count == 0
            ? Strings.Current["Core.Diagnostics.CrashDump.NoAssembly"]
            : string.Join(", ", named.Select(System.IO.Path.GetFileName));

        return UnresolvedFrameCount == 0
            ? Strings.Current.Format("Core.Diagnostics.CrashDump.StackNames", names)
            : Strings.Current.Format(
                "Core.Diagnostics.CrashDump.StackNamesWithGap", names, UnresolvedFrameCount, Stack.Count);
    }
}

public sealed record CrashDumpSearch(
    IReadOnlyList<CrashDumpReading> Dumps,
    IReadOnlyList<string> Searched,
    IReadOnlyList<string> Unreadable)
{
    public int FaultedCount => Dumps.Count(dump => dump.Outcome == CrashDumpOutcome.ManagedException);

    public int UnreadableDumpCount => Dumps.Count(dump => dump.Outcome == CrashDumpOutcome.Unreadable);

    // Unreadable carries two different failures and both have to reach this sentence: a dump that would
    // not open, and the folder itself refusing. An empty list of dumps because the folder could not be
    // enumerated is not the folder holding no dump, and saying so sent the user looking for nothing.
    public string Describe(string folder)
    {
        if (Dumps.Count == 0)
        {
            return Unreadable.Count == 0
                ? Strings.Current.Format("Core.Diagnostics.CrashDump.Search.NoneFound", folder)
                : Strings.Current.Format(
                    "Core.Diagnostics.CrashDump.Search.FolderUnreadable", folder, string.Join("; ", Unreadable));
        }

        var read = Strings.Current.Plural(
            "Core.Diagnostics.CrashDump.Search.Read",
            Dumps.Count, folder, FaultedCount, Dumps.Count - FaultedCount - UnreadableDumpCount, UnreadableDumpCount);

        var notInThatCount = Unreadable.Count - UnreadableDumpCount;

        return notInThatCount <= 0
            ? read
            : read + Strings.Current.Plural("Core.Diagnostics.CrashDump.Search.AlsoUnreadable", notInThatCount);
    }
}

// The crash evidence BEM never looked at. Windows Error Reporting writes a minidump of a process that
// faults into %LOCALAPPDATA%\CrashDumps and leaves it there, which makes it the one crash artifact on
// the machine that nothing races BEM to delete: the game's own uploader takes its report folder away
// about five seconds after a crash, and this folder it never touches.
//
// Nothing in here writes to that folder or removes anything from it.
public static class CrashDumps
{
    private const string DumpExtension = ".dmp";

    public static string GetDefaultFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrashDumps");

    // Windows names a dump "<process>.exe.<pid>.dmp", so the process it came from is in the file name
    // and no dump has to be opened to find out whose it is.
    public static string ProcessNameOf(string path)
    {
        var name = Path.GetFileName(path);
        var trimmed = name.EndsWith(DumpExtension, StringComparison.OrdinalIgnoreCase)
            ? name[..^DumpExtension.Length]
            : name;

        var lastDot = trimmed.LastIndexOf('.');

        if (lastDot > 0 && trimmed[(lastDot + 1)..].All(char.IsDigit))
            trimmed = trimmed[..lastDot];

        return Path.GetFileNameWithoutExtension(trimmed) is { Length: > 0 } withoutExtension
            ? withoutExtension
            : trimmed;
    }

    // The other half of the file name, and the key that ties a dump to a Windows fault record and to
    // the engine's own rgl_log file: all three carry the same process id, so a dump belongs to a fault
    // by fact rather than by having been written at about the same time. Zero when the name carries
    // none, which is a real case for a dump that was renamed.
    public static int ProcessIdOf(string path)
    {
        var name = Path.GetFileName(path);

        var trimmed = name.EndsWith(DumpExtension, StringComparison.OrdinalIgnoreCase)
            ? name[..^DumpExtension.Length]
            : name;

        var lastDot = trimmed.LastIndexOf('.');

        if (lastDot <= 0)
            return 0;

        var digits = trimmed[(lastDot + 1)..];

        return digits.Length > 0 && digits.All(char.IsDigit)
            && int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : 0;
    }

    public static CrashDumpSearch FindForTheGame(DateTimeOffset? writtenAfter = null) =>
        Find(GetDefaultFolder(), CrashArtifactPaths.IsWatchedProcess, writtenAfter);

    public static CrashDumpSearch Find(
        string folder,
        Func<string, bool> isWanted,
        DateTimeOffset? writtenAfter = null)
    {
        ArgumentNullException.ThrowIfNull(isWanted);

        var searched = new List<string> { folder };

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return new CrashDumpSearch([], searched, []);

        List<string> files;

        try
        {
            files = [.. Directory.EnumerateFiles(folder, "*" + DumpExtension, SearchOption.TopDirectoryOnly)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CrashDumpSearch([], searched, [$"{folder} could not be read: {ex.Message}"]);
        }

        var dumps = new List<CrashDumpReading>();
        var unreadable = new List<string>();

        foreach (var file in files)
        {
            if (!isWanted(ProcessNameOf(file)))
                continue;

            FileInfo info;

            try
            {
                info = new FileInfo(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add($"{file} could not be read: {ex.Message}");
                continue;
            }

            var written = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

            if (writtenAfter is { } after && written < after)
                continue;

            var reading = Read(file, ProcessNameOf(file), written, info.Length);

            dumps.Add(reading);

            if (reading.Outcome == CrashDumpOutcome.Unreadable)
                unreadable.Add($"{file} could not be read: {reading.Error}");
        }

        return new CrashDumpSearch(
            [.. dumps.OrderByDescending(d => d.Written).ThenBy(d => d.Path, StringComparer.OrdinalIgnoreCase)],
            searched,
            unreadable);
    }

    public static CrashDumpReading Read(string path)
    {
        FileInfo info;

        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CrashDumpReading(
                path,
                ProcessNameOf(path),
                DateTimeOffset.MinValue,
                0,
                CrashDumpOutcome.Unreadable,
                Error: ex.Message);
        }

        return Read(
            path,
            ProcessNameOf(path),
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            info.Length);
    }

    private static CrashDumpReading Read(
        string path,
        string processName,
        DateTimeOffset written,
        long sizeBytes)
    {
        try
        {
            using var target = DataTarget.LoadDump(path);

            // Never let reading a file off this machine reach the network. ClrMD's default file
            // locator follows _NT_SYMBOL_PATH to a symbol server, and a diagnostic that phones out
            // because that variable happens to be set is a cost nobody agreed to. The
            // debugging layer a Framework dump needs sits beside the runtime that produced it, on
            // this machine, and is found without any of that.
            target.FileLocator = null;

            foreach (var version in target.ClrVersions)
            {
                using var runtime = version.CreateRuntime();

                foreach (var thread in runtime.Threads)
                {
                    if (thread.CurrentException is not { } exception)
                        continue;

                    return new CrashDumpReading(
                        path,
                        processName,
                        written,
                        sizeBytes,
                        CrashDumpOutcome.ManagedException,
                        version.Version.ToString(),
                        exception.Type?.Name,
                        exception.Message,
                        [.. exception.StackTrace.Select(Describe)]);
                }
            }

            return new CrashDumpReading(
                path,
                processName,
                written,
                sizeBytes,
                CrashDumpOutcome.NoManagedException,
                target.ClrVersions.FirstOrDefault()?.Version.ToString());
        }
        // Deliberately everything the runtime will let go of. A dump is an arbitrary file written by
        // Windows about a process BEM never saw, read by a library that reaches into another runtime's
        // internals, and the list of ways that can go wrong is not one this code gets to enumerate. A
        // reader that takes the tab down with it is worse than a reader that says it could not read
        // one file, and there is no verdict here worth risking that for.
        //
        // What this cannot catch is a fault that ends the process rather than throwing. If that is ever
        // seen, the answer is to read dumps in a helper process rather than to widen this further.
        catch (Exception ex)
        {
            return new CrashDumpReading(
                path,
                processName,
                written,
                sizeBytes,
                CrashDumpOutcome.Unreadable,
                Error: ex.Message);
        }
    }

    private static CrashDumpFrame Describe(ClrStackFrame frame)
    {
        var method = frame.Method;

        if (method is null)
            return new CrashDumpFrame(null, null);

        var type = method.Type?.Name;

        return new CrashDumpFrame(
            string.IsNullOrEmpty(type) ? method.Name : $"{type}.{method.Name}",
            method.Type?.Module?.Name);
    }
}
