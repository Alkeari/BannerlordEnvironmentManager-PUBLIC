using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public enum SmartAppControlState
{
    // The policy or the log could not be read; nothing is claimed either way.
    Unreadable,
    Off,
    On,

    // Windows is still learning whether the owner's apps would run, and only logs what it would block.
    Evaluation
}

// One refusal in the CodeIntegrity log: the file Windows would not load, the process that asked, and whether the
// refusal was enforced (event 3033 or 3077) or only audited (3034 or 3076, Smart App Control's evaluation mode).
public sealed record CodeIntegrityBlock(string FilePath, string Process, int EventId)
{
    public bool Enforced => EventId is 3033 or 3077;

    public string FileName => FilePath[(FilePath.LastIndexOfAny(['\\', '/']) + 1)..];
}

public sealed record WindowsCodeIntegrity(SmartAppControlState State, IReadOnlyList<CodeIntegrityBlock> Blocks)
{
    public static WindowsCodeIntegrity None { get; } = new(SmartAppControlState.Off, []);
}

// Windows Smart App Control and WDAC policies refuse a DLL that is unsigned and not known to Microsoft. A mod DLL
// refused that way looks like a mystery crash, because the game only says the assembly would not load. The state
// is one registry value; the evidence is the CodeIntegrity operational log, whose messages read
//   Code Integrity determined that a process (\Device\HarddiskVolume3\...\Bannerlord.exe) attempted to load
//   \Device\HarddiskVolume3\...\Modules\X\bin\Win64_Shipping_Client\X.dll that did not meet the ... signing level requirements.
public static partial class SmartAppControl
{
    private const string PolicyKey = @"SYSTEM\CurrentControlSet\Control\CI\Policy";
    private const string PolicyValue = "VerifiedAndReputablePolicyState";
    private const string LogName = "Microsoft-Windows-CodeIntegrity/Operational";

    [GeneratedRegex(
        @"process \((?<process>[^)]*)\) attempted to load (?<file>.+?) that (?:did not meet|violated)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BlockMessage { get; }

    // The value is 0 off, 1 enforced, 2 evaluation. A machine that never had Smart App Control has no value,
    // which is off; anything else is a value this reading does not know.
    public static SmartAppControlState StateOf(int? policyValue) => policyValue switch
    {
        null or 0 => SmartAppControlState.Off,
        1 => SmartAppControlState.On,
        2 => SmartAppControlState.Evaluation,
        _ => SmartAppControlState.Unreadable
    };

    // The blocks a set of CodeIntegrity events name, one per file, enforced ones first-seen order kept. An event
    // whose message is not the block form is not evidence of one.
    public static IReadOnlyList<CodeIntegrityBlock> BlocksIn(IEnumerable<(int EventId, string Message)> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var found = new List<CodeIntegrityBlock>();

        foreach (var (id, message) in events)
        {
            if (id is not (3033 or 3034 or 3076 or 3077) || BlockMessage.Match(message) is not { Success: true } match)
                continue;

            var file = match.Groups["file"].Value.Trim().TrimEnd('.');

            if (file.Length == 0)
                continue;

            var existing = found.FindIndex(b => b.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase));
            var block = new CodeIntegrityBlock(file, match.Groups["process"].Value.Trim(), id);

            if (existing < 0)
                found.Add(block);
            else if (block.Enforced && !found[existing].Enforced)
                found[existing] = block;
        }

        return found;
    }

    // The developer build's stand-in for a machine with Smart App Control, since no agent turns the real policy on:
    // code-integrity-fixture.txt in the data root, first line "state=On|Off|Evaluation|Unreadable", every other
    // line "<event id>|<CodeIntegrity message>". Compiled out of the published exe.
    public const string FixtureFileName = "code-integrity-fixture.txt";

    public static WindowsCodeIntegrity FromFixture(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        var state = lines.Count > 0 && lines[0].StartsWith("state=", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<SmartAppControlState>(lines[0][6..].Trim(), true, out var parsed)
                ? parsed
                : SmartAppControlState.Unreadable;
        var events = new List<(int, string)>();

        foreach (var line in lines.Skip(1))
        {
            var bar = line.IndexOf('|');

            if (bar > 0 && int.TryParse(line[..bar], out var id))
                events.Add((id, line[(bar + 1)..]));
        }

        return new WindowsCodeIntegrity(state, BlocksIn(events));
    }

    public static WindowsCodeIntegrity Read(DateTimeOffset now, string? dataRootFolder = null, TimeSpan? window = null)
    {
#if DEV_BEM
        if (dataRootFolder is not null && Path.Combine(dataRootFolder, FixtureFileName) is { } fixture && File.Exists(fixture))
            return FromFixture(File.ReadAllText(fixture));
#endif
        _ = dataRootFolder;

        if (!OperatingSystem.IsWindows())
            return new WindowsCodeIntegrity(SmartAppControlState.Unreadable, []);

        return new WindowsCodeIntegrity(ReadState(), ReadBlocks(now - (window ?? RecentRunErrors.DefaultWindow)));
    }

    [SupportedOSPlatform("windows")]
    private static SmartAppControlState ReadState()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);

            if (key is null)
                return SmartAppControlState.Off;

            return key.GetValue(PolicyValue) is int value ? StateOf(value) : StateOf(null);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return SmartAppControlState.Unreadable;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<CodeIntegrityBlock> ReadBlocks(DateTimeOffset since)
    {
        try
        {
            var query = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                LogName,
                System.Diagnostics.Eventing.Reader.PathType.LogName,
                "*[System[(EventID=3033 or EventID=3034 or EventID=3076 or EventID=3077) and TimeCreated[@SystemTime>='"
                + since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture) + "']]]")
            {
                ReverseDirection = true
            };

            var events = new List<(int, string)>();

            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);

            for (var record = reader.ReadEvent(); record is not null && events.Count < 400; record = reader.ReadEvent())
            {
                using (record)
                    events.Add((record.Id, record.FormatDescription() ?? string.Empty));
            }

            return BlocksIn(events);
        }
        catch (Exception ex) when (ex is System.Diagnostics.Eventing.Reader.EventLogException or UnauthorizedAccessException or IOException)
        {
            // A log that cannot be read is no evidence, never a failed check.
            return [];
        }
    }
}
