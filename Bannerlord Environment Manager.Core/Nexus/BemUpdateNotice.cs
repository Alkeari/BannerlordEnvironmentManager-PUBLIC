using System.Text.Json;

namespace BannerlordEnvironmentManager.Core.Nexus;

// What BEM remembers between starts about announcing a newer version of itself.
public sealed record BemUpdateState(
    DateTimeOffset? LastCheckedUtc = null,
    string? LastPublishedVersion = null,
    string? SkippedVersion = null,
    DateTimeOffset? DismissedUntilUtc = null)
{
    public static BemUpdateState Empty { get; } = new();
}

// The rules that keep the notice from nagging. Nexus is asked at most once a day, and between checks
// the last answer is what is announced, so restarting BEM neither re-asks nor loses the notice. Closing
// the bar hides it until the next day's check; skipping hides that version and every older one for
// good, while a later version is announced again.
public static class BemUpdateNotice
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    // A last check in the future is a clock that moved backwards, and waiting for it would stop the
    // check for however far it moved.
    public static bool IsCheckDue(BemUpdateState state, DateTimeOffset nowUtc) =>
        state.LastCheckedUtc is not { } last || nowUtc - last >= CheckInterval || nowUtc < last;

    public static BemUpdateState Checked(BemUpdateState state, string publishedVersion, DateTimeOffset nowUtc) =>
        state with { LastCheckedUtc = nowUtc, LastPublishedVersion = publishedVersion };

    public static BemUpdateState Skipped(BemUpdateState state, string version) =>
        state with { SkippedVersion = version, DismissedUntilUtc = null };

    public static BemUpdateState Dismissed(BemUpdateState state, DateTimeOffset nowUtc) =>
        state with { DismissedUntilUtc = nowUtc + CheckInterval };

    public static string? VersionToAnnounce(BemUpdateState state, string? installedVersion, DateTimeOffset nowUtc)
    {
        if (state.LastPublishedVersion is not { } published
            || BemUpdateCheck.Compare(installedVersion, published) != BemUpdateVerdict.NewerAvailable)
            return null;

        if (state.SkippedVersion is { } skipped
            && BemUpdateCheck.Compare(skipped, published) == BemUpdateVerdict.UpToDate)
            return null;

        return state.DismissedUntilUtc is { } until && nowUtc < until ? null : published;
    }
}

public sealed class BemUpdateStateStore(string filePath)
{
    public const string FileName = "bem-update-state.json";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string FilePath { get; } = filePath;

    public BemUpdateState Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<BemUpdateState>(File.ReadAllText(FilePath), Format) ?? BemUpdateState.Empty
                : BemUpdateState.Empty;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return BemUpdateState.Empty;
        }
    }

    // A state that cannot be written costs at most one more check or one more notice, never the start.
    public void Save(BemUpdateState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, Format));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}
