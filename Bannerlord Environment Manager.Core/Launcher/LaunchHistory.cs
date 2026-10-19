using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Launcher;

public enum LaunchOutcome
{
    // BEM had no way to see the end of the run. A launcher target starts a launcher and hands off to a
    // game process BEM never started, and Steam starts one BEM holds no handle on at all. This is the
    // honest answer for those, and it is not the same statement as a run that ended badly.
    Unknown,

    // BEM watched the process and has not seen it end yet. A record left in this state is rewritten to
    // Unknown the next time a launch is recorded, because BEM being closed first is exactly the case
    // where nobody ever saw the end.
    Running,

    ExitedCleanly,

    // A non-zero exit code that Windows did not choose. Ending the game from Task Manager looks like
    // this, so on its own it is not evidence of a crash.
    ExitedWithError,

    // Windows itself ended the process on an unhandled exception. NTSTATUS failure codes are the only
    // exit codes that say so; every other non-zero code is the program's own choice.
    Crashed
}

// The identity of a run, for the one question worth asking before another one: has this exact set of
// modules, in this exact order, been launched before and how did it go. Only the enabled modules are
// in it, because those are the ones that reach the game, and the order is part of the text because
// two orders of the same set are two different runs.
public static class LoadOrderFingerprint
{
    public static string Of(LoadOrderSnapshot order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var text = string.Join('\n', order.Entries.Where(e => e.IsEnabled).Select(e => e.Id));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }
}

public sealed record LaunchRecord(
    string Id,
    DateTime StartedUtc,
    LaunchTargetKind TargetKind,
    string TargetName,
    string OrderFingerprint,
    LoadOrderSnapshot Order,
    LaunchOutcome Outcome = LaunchOutcome.Running,
    DateTime? EndedUtc = null,
    int? ExitCode = null,
    string? UnknownReason = null)
{
    public int EnabledCount => Order.EnabledCount;

    public TimeSpan? Ran => EndedUtc is { } ended ? ended - StartedUtc : null;

    // Only the direct targets are the game itself. A launcher target exits as soon as it has started
    // the game, so watching it would time a launcher and call it a play session.
    public static bool CanSeeTheEnd(LaunchTargetKind kind) =>
        kind is LaunchTargetKind.BlseStandalone or LaunchTargetKind.GameExecutable;

    public static LaunchOutcome Classify(int exitCode) => exitCode == 0
        ? LaunchOutcome.ExitedCleanly
        : unchecked((uint)exitCode) >= 0xC0000000u
            ? LaunchOutcome.Crashed
            : LaunchOutcome.ExitedWithError;

    public string Describe()
    {
        var when = StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var ran = Ran is { } length ? $", ran {length:hh\\:mm\\:ss}" : string.Empty;

        return Strings.Current.Plural(
            "Core.Launcher.History.Describe", EnabledCount, when, TargetName, ran, DescribeOutcome());
    }

    public string DescribeOutcome() => Outcome switch
    {
        LaunchOutcome.Running => Strings.Current["Core.Launcher.History.Outcome.Running"],
        LaunchOutcome.ExitedCleanly => Strings.Current["Core.Launcher.History.Outcome.ExitedCleanly"],
        LaunchOutcome.Crashed =>
            Strings.Current.Format("Core.Launcher.History.Outcome.Crashed", $"{ExitCode:X8}"),
        LaunchOutcome.ExitedWithError =>
            Strings.Current.Format("Core.Launcher.History.Outcome.ExitedWithError", ExitCode),
        _ => UnknownReason ?? Strings.Current["Core.Launcher.History.Outcome.UnknownFallback"]
    };
}

// One file, newest last, capped. A launch log that grows without limit is a file nobody prunes, and
// the only question anyone asks of it is about the most recent runs.
public sealed class LaunchHistoryStore(string path)
{
    private const int Keep = 100;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private sealed record RecordFile(
        string Id,
        DateTime StartedUtc,
        LaunchTargetKind TargetKind,
        string TargetName,
        string OrderFingerprint,
        List<LoadOrderSnapshotEntry> Order,
        LaunchOutcome Outcome,
        DateTime? EndedUtc,
        int? ExitCode,
        string? UnknownReason);

    private sealed record HistoryFile(List<RecordFile> Launches);

    public string FilePath { get; } = path;

    // Per version: this file describes one specific load order, so a machine-wide copy showed one
    // version's answer while another was selected. The resting version keeps the path it has always
    // used; every other version reads and writes its own.
    public static string GetDefaultPath(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, "launches.json");

    public IReadOnlyList<LaunchRecord> Read()
    {
        if (!File.Exists(FilePath))
            return [];

        try
        {
            var file = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(FilePath));

            return file?.Launches is null ? [] : [.. file.Launches.Select(ToRecord)];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Records the start of a run and returns what was written. Any earlier record still marked Running
    // is rewritten to Unknown in the same pass: BEM was closed before the game was, and claiming the
    // run is still going would be a worse answer than admitting nobody saw it end.
    public LaunchRecord Start(LaunchRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var kept = Read()
            .Select(r => r.Outcome == LaunchOutcome.Running
                ? r with
                {
                    Outcome = LaunchOutcome.Unknown,
                    UnknownReason = Strings.Current["Core.Launcher.History.ClosedBeforeGame"]
                }
                : r)
            .ToList();

        kept.Add(record);

        Write(kept);

        return record;
    }

    public bool Finish(string id, int exitCode, DateTime endedUtc)
    {
        var kept = Read().ToList();
        var index = kept.FindIndex(r => string.Equals(r.Id, id, StringComparison.Ordinal));

        if (index < 0)
            return false;

        kept[index] = kept[index] with
        {
            Outcome = LaunchRecord.Classify(exitCode),
            EndedUtc = endedUtc,
            ExitCode = exitCode
        };

        Write(kept);

        return true;
    }

    // The end of a run BEM started but could not watch. Said plainly rather than left as Running,
    // which would otherwise read as a game still open hours later.
    public bool CouldNotSeeTheEnd(string id, string reason)
    {
        var kept = Read().ToList();
        var index = kept.FindIndex(r => string.Equals(r.Id, id, StringComparison.Ordinal));

        if (index < 0)
            return false;

        kept[index] = kept[index] with { Outcome = LaunchOutcome.Unknown, UnknownReason = reason };

        Write(kept);

        return true;
    }

    private void Write(List<LaunchRecord> records)
    {
        var trimmed = records.Count > Keep ? records.Skip(records.Count - Keep).ToList() : records;

        try
        {
            var folder = Path.GetDirectoryName(FilePath);

            if (!string.IsNullOrWhiteSpace(folder))
                Directory.CreateDirectory(folder);

            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(new HistoryFile([.. trimmed.Select(ToFile)]), Format));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A launch log that cannot be written must never stop a launch. The next read simply finds
            // one fewer run, which the preflight already reports as not knowing rather than as clear.
        }
    }

    private static RecordFile ToFile(LaunchRecord record) => new(
        record.Id,
        record.StartedUtc,
        record.TargetKind,
        record.TargetName,
        record.OrderFingerprint,
        [.. record.Order.Entries],
        record.Outcome,
        record.EndedUtc,
        record.ExitCode,
        record.UnknownReason);

    private static LaunchRecord ToRecord(RecordFile file) => new(
        file.Id,
        file.StartedUtc,
        file.TargetKind,
        file.TargetName ?? string.Empty,
        file.OrderFingerprint ?? string.Empty,
        new LoadOrderSnapshot(file.Order ?? []),
        file.Outcome,
        file.EndedUtc,
        file.ExitCode,
        file.UnknownReason);
}
