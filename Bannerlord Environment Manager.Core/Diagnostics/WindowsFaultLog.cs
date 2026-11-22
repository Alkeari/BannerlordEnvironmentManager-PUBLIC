using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public enum WindowsFaultKind
{
    // Application Error, 1000. The process faulted and Windows recorded the code, the module it was
    // in and the offset within that module.
    Fault,

    // .NET Runtime, 1026. A managed exception nobody caught, with the stack the runtime printed.
    ManagedException,

    // .NET Runtime, 1023. The runtime itself gave up, which is not the same as an exception escaping.
    RuntimeError,

    // Application Hang, 1002. The process stopped answering and Windows closed it. Nothing faulted.
    Hang,

    // Windows Error Reporting, 1001. The report Windows filed about a fault, alongside the fault.
    ErrorReport
}

// What an exception code means, in the plainest words the code alone supports. A raw hex number helps
// nobody, and a translation that claims more than the number says helps less than nothing: a fail-fast
// is raised by four different things and the code does not say which, so the sentence says that too.
public sealed record WindowsFaultCode(uint Value, string Name, string Meaning, bool IsKnown = true)
{
    public string Hex => "0x" + Value.ToString("X8", CultureInfo.InvariantCulture);

    public string Describe() => Strings.Current.Format("Core.Diagnostics.WindowsFaultCode.Describe", Hex, Name, Meaning);
}

public static class WindowsFaultCodes
{
    public static WindowsFaultCode Translate(uint value) => value switch
    {
        0xC0000005 => new WindowsFaultCode(value, "access violation",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.AccessViolation"]),
        0xC0000006 => new WindowsFaultCode(value, "in-page error",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.InPageError"]),
        0xC000001D => new WindowsFaultCode(value, "illegal instruction",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.IllegalInstruction"]),
        0xC0000025 => new WindowsFaultCode(value, "noncontinuable exception",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.NoncontinuableException"]),
        0xC000008C => new WindowsFaultCode(value, "array bounds exceeded",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.ArrayBoundsExceeded"]),
        0xC0000090 => new WindowsFaultCode(value, "floating point invalid operation",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.FloatingPointInvalidOperation"]),
        0xC0000094 => new WindowsFaultCode(value, "integer divide by zero",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.IntegerDivideByZero"]),
        0xC00000FD => new WindowsFaultCode(value, "stack overflow",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.StackOverflow"]),
        0xC0000135 => new WindowsFaultCode(value, "a DLL was not found",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.DllNotFound"]),
        0xC0000142 => new WindowsFaultCode(value, "DLL initialization failed",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.DllInitFailed"]),
        0xC000027B => new WindowsFaultCode(value, "stowed exception",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.StowedException"]),
        0xC0000374 => new WindowsFaultCode(value, "heap corruption",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.HeapCorruption"]),
        0xC0000409 => new WindowsFaultCode(value, "fail-fast",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.FailFast"]),
        0xE0434352 => new WindowsFaultCode(value, "a managed exception nobody caught",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.ManagedExceptionUncaught"]),
        0xE0434F4D => new WindowsFaultCode(value, "a managed exception nobody caught, older runtime",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.ManagedExceptionUncaughtOlder"]),
        0xE06D7363 => new WindowsFaultCode(value, "an unhandled C++ exception",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.UnhandledCppException"]),
        0x80000003 => new WindowsFaultCode(value, "a breakpoint",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.Breakpoint"]),
        0x40000015 => new WindowsFaultCode(value, "fatal application exit",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.FatalApplicationExit"]),
        0xCFFFFFFF => new WindowsFaultCode(value, "terminated by error reporting",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.TerminatedByErrorReporting"]),
        _ => new WindowsFaultCode(value, "a code BEM does not have a name for",
            Strings.Current["Core.Diagnostics.WindowsFaultCode.Meaning.Unknown"], false)
    };

    // Every code this file can name, so the set can be shown whole rather than one value at a time.
    public static IReadOnlyList<WindowsFaultCode> Known { get; } =
    [
        .. new uint[]
            {
                0xC0000005, 0xC0000006, 0xC000001D, 0xC0000025, 0xC000008C, 0xC0000090, 0xC0000094,
                0xC00000FD, 0xC0000135, 0xC0000142, 0xC000027B, 0xC0000374, 0xC0000409, 0xE0434352,
                0xE0434F4D, 0xE06D7363, 0x80000003, 0x40000015, 0xCFFFFFFF
            }
            .Select(Translate)
    ];

    public static uint? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..];

        return uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}

// One thing Windows wrote down about one process ending. Every field here was read out of the record;
// nothing is inferred, and nothing in this type names a mod, because a fault record cannot.
public sealed record WindowsFaultRecord(
    WindowsFaultKind Kind,
    string Provider,
    int EventId,
    DateTimeOffset When,
    string ProcessName,
    string ProcessPath = "",
    string ProcessVersion = "",
    int ProcessId = 0,
    DateTimeOffset? ProcessStarted = null,
    string FaultingModule = "",
    string FaultingModulePath = "",
    WindowsFaultCode? Code = null,
    string FaultOffset = "",
    string Note = "",
    string ManagedStack = "")
{
    // The name the rest of BEM matches processes by, which is the file name with no extension. The
    // event log writes "Bannerlord.BLSE.Standalone.exe" and CrashDumps writes the same name off a file
    // called "Bannerlord.BLSE.Standalone.exe.41732.dmp".
    public string ProcessStem => System.IO.Path.GetFileNameWithoutExtension(ProcessName);

    public bool HasProcessId => ProcessId > 0;

    // What makes two of these the same crash. The code and the offset within the faulting module are
    // the whole of it: the same code at the same offset twice is the same instruction failing twice,
    // which is the single most useful thing this feature can say.
    public string Signature => Kind switch
    {
        WindowsFaultKind.Fault or WindowsFaultKind.ErrorReport =>
            $"{ProcessStem}|{Code?.Hex ?? "no code"}|{FaultingModule}|{FaultOffset}",
        WindowsFaultKind.Hang => $"{ProcessStem}|hang|{Note}",
        _ => $"{ProcessStem}|{Kind}|{FirstStackLine}"
    };

    public string FirstStackLine => ManagedStack
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault(line => line.StartsWith("at ", StringComparison.OrdinalIgnoreCase))
        ?? string.Empty;

    public string Headline() => Kind switch
    {
        WindowsFaultKind.Fault => Code is { } code
            ? Strings.Current.Format(
                "Core.Diagnostics.WindowsFaultRecord.Headline.Fault", ProcessName, code.Hex, ModuleOrUnknown, OffsetSuffix)
            : Strings.Current.Format("Core.Diagnostics.WindowsFaultRecord.Headline.FaultNoCode", ProcessName),
        WindowsFaultKind.ManagedException =>
            Strings.Current.Format("Core.Diagnostics.WindowsFaultRecord.Headline.ManagedException", ProcessName),
        WindowsFaultKind.RuntimeError =>
            Strings.Current.Format("Core.Diagnostics.WindowsFaultRecord.Headline.RuntimeError", ProcessName),
        WindowsFaultKind.Hang =>
            Strings.Current.Format("Core.Diagnostics.WindowsFaultRecord.Headline.Hang", ProcessName),
        _ => Code is { } reported
            ? Strings.Current.Format("Core.Diagnostics.WindowsFaultRecord.Headline.ErrorReport", ProcessName, reported.Hex)
            : Strings.Current.Format("Core.Diagnostics.WindowsFaultRecord.Headline.ErrorReportNoCode", ProcessName)
    };

    private string ModuleOrUnknown => FaultingModule.Length == 0
        || string.Equals(FaultingModule, "unknown", StringComparison.OrdinalIgnoreCase)
            ? Strings.Current["Core.Diagnostics.WindowsFaultRecord.UnknownModule"]
            : FaultingModule;

    private string OffsetSuffix => FaultOffset.Length == 0
        ? string.Empty
        : Strings.Current.Format("Core.Diagnostics.WindowsFaultRecord.OffsetSuffix", FaultOffset);

    public string When24 => When.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    // The sentence this whole feature has to be able to say without flinching. Windows watched a
    // process die; it did not watch a mod cause it, and the record contains nothing that could.
    public static string NamesNoMod => Strings.Current["Core.Diagnostics.WindowsFaultRecord.NamesNoMod"];

    public string Describe()
    {
        var lines = new List<string> { Headline(), When24 };

        if (HasProcessId)
        {
            lines.Add(Strings.Current.Format(
                "Core.Diagnostics.WindowsFaultRecord.ProcessIdLine", ProcessId.ToString(CultureInfo.InvariantCulture)));
        }

        if (ProcessStarted is { } started)
        {
            lines.Add(Strings.Current.Format(
                "Core.Diagnostics.WindowsFaultRecord.StartedLine",
                started.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        }

        if (ProcessVersion.Length > 0)
            lines.Add(Strings.Current.Format("Core.Diagnostics.WindowsFaultRecord.VersionLine", ProcessVersion));

        if (Code is { } code)
            lines.Add(code.Describe());

        if (FaultOffset.Length > 0)
            lines.Add(Strings.Current.Format("Core.Diagnostics.WindowsFaultRecord.FaultOffsetLine", FaultOffset, ModuleOrUnknown));

        if (FaultingModulePath.Length > 0)
            lines.Add(FaultingModulePath);

        if (ProcessPath.Length > 0)
            lines.Add(ProcessPath);

        if (Note.Length > 0)
            lines.Add(Note);

        if (ManagedStack.Length > 0)
            lines.Add(ManagedStack);

        lines.Add(Strings.Current.Format(
            "Core.Diagnostics.WindowsFaultRecord.ProviderEventLine",
            Provider, EventId.ToString(CultureInfo.InvariantCulture), NamesNoMod));

        return string.Join(Environment.NewLine, lines);
    }
}

// The same crash, seen more than once. This is the fact no other source gives: five
// records carrying one code at one offset is a crash that repeats identically, and a crash that
// repeats identically is one an experiment can find.
public sealed record WindowsFaultGroup(string Signature, IReadOnlyList<WindowsFaultRecord> Records)
{
    public WindowsFaultRecord Newest => Records[0];

    public int Count => Records.Count;

    public DateTimeOffset First => Records[^1].When;

    public DateTimeOffset Last => Records[0].When;

    public string Title => Newest.Headline();

    public IReadOnlyList<int> ProcessIds =>
        [.. Records.Where(r => r.HasProcessId).Select(r => r.ProcessId)];

    public string Describe()
    {
        if (Count == 1)
            return Strings.Current.Format("Core.Diagnostics.WindowsFaultGroup.SeenOnce", Newest.When24, WindowsFaultRecord.NamesNoMod);

        var span = First == Last
            ? Strings.Current.Format(
                "Core.Diagnostics.WindowsFaultGroup.Span.AllAt",
                Last.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            : Strings.Current.Format(
                "Core.Diagnostics.WindowsFaultGroup.Span.Range",
                First.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Last.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

        var repeatable = Newest.Kind == WindowsFaultKind.Fault && Newest.FaultOffset.Length > 0
            ? Strings.Current["Core.Diagnostics.WindowsFaultGroup.Repeatable.Deterministic"]
            : Strings.Current["Core.Diagnostics.WindowsFaultGroup.Repeatable.SameSignature"];

        return Strings.Current.Format(
            "Core.Diagnostics.WindowsFaultGroup.SeenMultiple",
            Count.ToString(CultureInfo.InvariantCulture), span, repeatable, WindowsFaultRecord.NamesNoMod);
    }
}

public sealed record WindowsFaultLogReading(
    bool CouldRead,
    string Error,
    IReadOnlyList<WindowsFaultRecord> Records,
    DateTimeOffset? OldestRecordInLog,
    bool HitLimit)
{
    // Newest first within a group, and the group with the newest record first. The crash the user is
    // asking about is almost always the one that just happened.
    public IReadOnlyList<WindowsFaultGroup> Groups =>
    [
        .. Records
            .GroupBy(record => record.Signature, StringComparer.Ordinal)
            .Select(group => new WindowsFaultGroup(
                group.Key,
                [.. group.OrderByDescending(record => record.When)]))
            .OrderByDescending(group => group.Last)
    ];

    // Three different statements, and they must never read the same. "Could not look" is not "found
    // nothing", and "found nothing in a log that only goes back to Tuesday" is not "nothing happened".
    public string Describe()
    {
        if (!CouldRead)
            return Strings.Current.Format("Core.Diagnostics.WindowsFaultLogReading.CouldNotRead", Error);

        var reach = OldestRecordInLog is { } oldest
            ? Strings.Current.Format(
                "Core.Diagnostics.WindowsFaultLogReading.Reach.Known",
                oldest.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            : Strings.Current["Core.Diagnostics.WindowsFaultLogReading.Reach.Unknown"];

        if (Records.Count == 0)
            return Strings.Current["Core.Diagnostics.WindowsFaultLogReading.NoFaults"] + reach;

        var groups = Groups;

        var counted = Strings.Current.Plural(
            "Core.Diagnostics.WindowsFaultLogReading.Counted",
            Records.Count,
            groups.Count.ToString(CultureInfo.InvariantCulture),
            Records[0].When24);

        var capped = HitLimit
            ? Strings.Current["Core.Diagnostics.WindowsFaultLogReading.HitLimit"]
            : string.Empty;

        return counted + reach + capped + " " + WindowsFaultRecord.NamesNoMod;
    }
}

// The Windows Application event log, read for the processes BEM knows it can launch. This is the one
// crash source that sees a run which wrote no log and left no managed exception in its dump, and it was
// the only thing that separated two failures that had been read as one.
//
// Reading is a read. Nothing here writes an event, clears a log, or asks for elevation: the Application
// log is readable by any user, which is checked by the fact that this reads it without any.
public static class WindowsFaultLog
{
    public const string LogName = "Application";

    // The exact provider and event id pairs, and nothing else. Broadening it would put every
    // application on the machine into a list about the game.
    private static readonly (string Provider, int EventId, WindowsFaultKind Kind)[] Wanted =
    [
        ("Application Error", 1000, WindowsFaultKind.Fault),
        ("Windows Error Reporting", 1001, WindowsFaultKind.ErrorReport),
        ("Application Hang", 1002, WindowsFaultKind.Hang),
        (".NET Runtime", 1023, WindowsFaultKind.RuntimeError),
        (".NET Runtime", 1026, WindowsFaultKind.ManagedException)
    ];

    // The service evaluates this, so the 46000 records this log holds on a real machine never
    // cross the process boundary. Provider and id are paired up again in code below, because this one
    // predicate would also match, say, an Application Error carrying id 1002.
    private const string Selector = """
        *[System[
          (Provider[@Name='Application Error'] or Provider[@Name='Application Hang']
           or Provider[@Name='.NET Runtime'] or Provider[@Name='Windows Error Reporting'])
          and (EventID=1000 or EventID=1001 or EventID=1002 or EventID=1023 or EventID=1026)
        ]]
        """;

    private const int DefaultLimit = 400;

    public static string Describe(IReadOnlyList<(string Provider, int EventId)> read) =>
        string.Join(", ", read.Select(pair => $"{pair.Provider} {pair.EventId}"));

    // What BEM reads, said out loud so the list is inspectable rather than buried in a query string.
    public static IReadOnlyList<(string Provider, int EventId)> Sources =>
        [.. Wanted.Select(w => (w.Provider, w.EventId))];

    public static bool IsGameProcess(string? executable) =>
        CrashArtifactPaths.IsWatchedProcess(Path.GetFileNameWithoutExtension(executable ?? string.Empty));

    public static WindowsFaultLogReading ReadForTheGame(int limit = DefaultLimit) =>
        Read(IsGameProcess, limit);

    public static WindowsFaultLogReading Read(Func<string, bool> isWanted, int limit = DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(isWanted);

        if (!OperatingSystem.IsWindows())
        {
            return new WindowsFaultLogReading(
                false, "the Windows event log only exists on Windows", [], null, false);
        }

        return ReadOnWindows(isWanted, Math.Max(1, limit));
    }

    [SupportedOSPlatform("windows")]
    private static WindowsFaultLogReading ReadOnWindows(Func<string, bool> isWanted, int limit)
    {
        var records = new List<WindowsFaultRecord>();
        var hitLimit = false;

        try
        {
            var query = new EventLogQuery(LogName, PathType.LogName, Selector) { ReverseDirection = true };

            using var reader = new EventLogReader(query);

            while (records.Count < limit)
            {
                EventRecord? entry;

                try
                {
                    entry = reader.ReadEvent();
                }
                // One record that will not decode is one record, never the whole read. The log holds
                // events from every program on the machine and any of them can carry a template BEM's
                // reader chokes on.
                catch (EventLogException)
                {
                    continue;
                }

                if (entry is null)
                    break;

                using (entry)
                {
                    if (Parse(entry, isWanted) is { } record)
                        records.Add(record);
                }
            }

            hitLimit = records.Count >= limit;
        }
        // Deliberately broad. This reaches an operating system service through a native interop layer
        // to read arbitrary records written by programs BEM has never heard of, and a diagnostic that
        // takes the tab down with it is worse than one that says it could not read.
        catch (Exception ex)
        {
            return new WindowsFaultLogReading(false, ex.Message, [], OldestRecord(), false);
        }

        return new WindowsFaultLogReading(
            true,
            string.Empty,
            [.. records.OrderByDescending(record => record.When)],
            OldestRecord(),
            hitLimit);
    }

    // How far back the log still reaches, read off the oldest record rather than off the log's creation
    // time: the log is circular, so it was created long before the oldest entry it still holds. Without
    // this, "no fault records" reads as "nothing happened" when it may only mean "not any more".
    [SupportedOSPlatform("windows")]
    private static DateTimeOffset? OldestRecord()
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(LogName, PathType.LogName));
            using var entry = reader.ReadEvent();

            return entry?.TimeCreated is { } created ? new DateTimeOffset(created.ToUniversalTime(), TimeSpan.Zero) : null;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static WindowsFaultRecord? Parse(EventRecord entry, Func<string, bool> isWanted)
    {
        var kind = Wanted
            .Where(w => w.EventId == entry.Id
                && string.Equals(w.Provider, entry.ProviderName, StringComparison.OrdinalIgnoreCase))
            .Select(w => (WindowsFaultKind?)w.Kind)
            .FirstOrDefault();

        if (kind is not { } wanted || entry.TimeCreated is not { } created)
            return null;

        IList<EventProperty> properties;

        try
        {
            properties = entry.Properties;
        }
        catch (EventLogException)
        {
            return null;
        }

        var when = new DateTimeOffset(created.ToUniversalTime(), TimeSpan.Zero);

        return wanted switch
        {
            WindowsFaultKind.Fault => Fault(entry, properties, when, isWanted),
            WindowsFaultKind.Hang => Hang(entry, properties, when, isWanted),
            WindowsFaultKind.ErrorReport => ErrorReport(entry, properties, when, isWanted),
            _ => Runtime(entry, properties, when, wanted, isWanted)
        };
    }

    // Application Error, 1000. Fifteen properties in a fixed order, of which BEM reads eight. Verified
    // against a real log rather than against documentation.
    [SupportedOSPlatform("windows")]
    private static WindowsFaultRecord? Fault(
        EventRecord entry,
        IList<EventProperty> properties,
        DateTimeOffset when,
        Func<string, bool> isWanted)
    {
        var name = Text(properties, 0);

        if (name.Length == 0 || !isWanted(name))
            return null;

        return new WindowsFaultRecord(
            WindowsFaultKind.Fault,
            entry.ProviderName ?? string.Empty,
            entry.Id,
            when,
            name,
            Text(properties, 10),
            Text(properties, 1),
            Number(properties, 8),
            FileTime(properties, 9),
            Text(properties, 3),
            Text(properties, 11),
            WindowsFaultCodes.Parse(Text(properties, 6)) is { } code
                ? WindowsFaultCodes.Translate(code)
                : null,
            Offset(Text(properties, 7)));
    }

    // Application Hang, 1002. Nothing faulted here, so there is no code and no offset, and the record
    // is worth showing precisely because it says a different thing: the process was alive and not
    // answering.
    [SupportedOSPlatform("windows")]
    private static WindowsFaultRecord? Hang(
        EventRecord entry,
        IList<EventProperty> properties,
        DateTimeOffset when,
        Func<string, bool> isWanted)
    {
        var name = Text(properties, 0);

        if (name.Length == 0 || !isWanted(name))
            return null;

        var hangType = Text(properties, 9);

        return new WindowsFaultRecord(
            WindowsFaultKind.Hang,
            entry.ProviderName ?? string.Empty,
            entry.Id,
            when,
            name,
            Text(properties, 5),
            Text(properties, 1),
            Number(properties, 2),
            FileTime(properties, 3),
            Note: hangType.Length > 0
                ? Strings.Current.Format("Core.Diagnostics.WindowsFaultLog.Note.HangWithType", hangType)
                : Strings.Current["Core.Diagnostics.WindowsFaultLog.Note.Hang"]);
    }

    // .NET Runtime, 1023 and 1026. One property holding the whole block of text, so everything here is
    // read out of that text and anything the text does not carry stays empty. There is no process id in
    // it at all, which is why a record of this kind can only ever be tied to other evidence by time.
    [SupportedOSPlatform("windows")]
    private static WindowsFaultRecord? Runtime(
        EventRecord entry,
        IList<EventProperty> properties,
        DateTimeOffset when,
        WindowsFaultKind kind,
        Func<string, bool> isWanted)
    {
        var body = Text(properties, 0);

        if (body.Length == 0)
            return null;

        var lines = body.Split('\n', StringSplitOptions.None);

        var name = Value(lines, "Application:");

        if (name.Length == 0 || !isWanted(name))
            return null;

        var runtime = Value(lines, "CoreCLR Version:");

        if (runtime.Length == 0)
            runtime = Value(lines, "Framework Version:");

        var description = Value(lines, "Description:");

        var exception = body.IndexOf("Exception Info:", StringComparison.OrdinalIgnoreCase);

        var note = new List<string>();

        if (runtime.Length > 0)
            note.Add(Strings.Current.Format("Core.Diagnostics.WindowsFaultLog.Note.Runtime", runtime));

        if (description.Length > 0)
            note.Add(description);

        return new WindowsFaultRecord(
            kind,
            entry.ProviderName ?? string.Empty,
            entry.Id,
            when,
            name,
            ProcessVersion: Value(lines, ".NET Version:"),
            Note: string.Join(" ", note),
            ManagedStack: exception >= 0 ? body[exception..].TrimEnd() : string.Empty);
    }

    // Windows Error Reporting, 1001. Its properties are positional the way the others are, but the
    // logs measured here hold none of these at all, so nothing here trusts a position: the process name is
    // whichever property is an executable name, and a code is taken only when the text is one BEM can
    // already name. Anything it cannot find stays empty rather than being guessed at.
    [SupportedOSPlatform("windows")]
    private static WindowsFaultRecord? ErrorReport(
        EventRecord entry,
        IList<EventProperty> properties,
        DateTimeOffset when,
        Func<string, bool> isWanted)
    {
        var values = new List<string>();

        for (var index = 0; index < properties.Count; index++)
            values.Add(Text(properties, index));

        var name = values.FirstOrDefault(value =>
            value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !value.Contains(Path.DirectorySeparatorChar)
            && isWanted(value));

        if (name is null)
            return null;

        var path = values.FirstOrDefault(value =>
            value.EndsWith(name, StringComparison.OrdinalIgnoreCase) && value.Length > name.Length)
            ?? string.Empty;

        var code = values
            .Select(WindowsFaultCodes.Parse)
            .Where(parsed => parsed is not null)
            .Select(parsed => WindowsFaultCodes.Translate(parsed!.Value))
            .FirstOrDefault(translated => translated.IsKnown);

        var offset = values.FirstOrDefault(value =>
            value.Length == 16 && value.All(Uri.IsHexDigit)) ?? string.Empty;

        return new WindowsFaultRecord(
            WindowsFaultKind.ErrorReport,
            entry.ProviderName ?? string.Empty,
            entry.Id,
            when,
            name,
            path,
            Code: code,
            FaultOffset: Offset(offset),
            Note: Strings.Current["Core.Diagnostics.WindowsFaultLog.Note.ErrorReport"]);
    }

    private static string Value(string[] lines, string label)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                return trimmed[label.Length..].Trim();
        }

        return string.Empty;
    }

    private static string Offset(string raw) =>
        raw.Trim() is { Length: > 0 } trimmed
            ? trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? trimmed : "0x" + trimmed
            : string.Empty;

    [SupportedOSPlatform("windows")]
    private static string Text(IList<EventProperty> properties, int index) =>
        index >= 0 && index < properties.Count
            ? Convert.ToString(properties[index].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty
            : string.Empty;

    [SupportedOSPlatform("windows")]
    private static int Number(IList<EventProperty> properties, int index)
    {
        if (index < 0 || index >= properties.Count)
            return 0;

        try
        {
            return properties[index].Value switch
            {
                int value => value,
                uint value => value <= int.MaxValue ? (int)value : 0,
                long value => value is > 0 and <= int.MaxValue ? (int)value : 0,
                // Windows writes a process id as text in some templates and as a number in others, and
                // the text form is hexadecimal with no prefix.
                string text => int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0,
                _ => 0
            };
        }
        catch (EventLogException)
        {
            return 0;
        }
    }

    [SupportedOSPlatform("windows")]
    private static DateTimeOffset? FileTime(IList<EventProperty> properties, int index)
    {
        if (index < 0 || index >= properties.Count)
            return null;

        try
        {
            long ticks = properties[index].Value switch
            {
                ulong value when value <= long.MaxValue => (long)value,
                long value => value,
                _ => 0
            };

            return ticks <= 0
                ? null
                : new DateTimeOffset(DateTime.FromFileTimeUtc(ticks), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is EventLogException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
