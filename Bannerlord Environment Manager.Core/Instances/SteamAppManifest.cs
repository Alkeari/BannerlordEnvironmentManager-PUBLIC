using System.Globalization;
using System.Text.RegularExpressions;

namespace BannerlordEnvironmentManager.Core.Instances;

// Steam's manifest is read, never written. It is the only place that says which branch an install is
// pinned to and whether an update is already queued for it, which is what lets BEM warn before a
// version disappears rather than after.
public sealed partial record SteamAppManifest(
    string BuildId,
    string Branch,
    string InstallDir,
    bool UpdatePending,
    int AutoUpdateBehavior)
{
    public const string PublicBranch = "public";

    // StateFlags is a bitmask, not an enum value: a running game plausibly carries other bits
    // alongside 4 (fully installed), so testing for "not exactly 4" flags a game that is simply
    // open as having an update pending. Only these bits mean Steam actually has an update queued
    // or in progress; the numeric values come from Steam's own StateFlags enum.
    private const int UpdateRequired = 2;
    private const int UpdateRunning = 256;
    private const int UpdatePaused = 512;
    private const int UpdateStarted = 1024;
    private const int Downloading = 1048576;
    private const int Staging = 2097152;
    private const int Committing = 4194304;

    private const int UpdatePendingMask =
        UpdateRequired | UpdateRunning | UpdatePaused | UpdateStarted | Downloading | Staging | Committing;

    [GeneratedRegex("""^\s*"(?<key>[^"]+)"\s+"(?<value>[^"]*)"\s*$""", RegexOptions.Multiline)]
    private static partial Regex EntryPattern { get; }

    public static SteamAppManifest? Read(string acfPath)
    {
        if (string.IsNullOrWhiteSpace(acfPath) || !File.Exists(acfPath))
            return null;

        string contents;

        try
        {
            contents = File.ReadAllText(acfPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in EntryPattern.Matches(contents))
            values.TryAdd(match.Groups["key"].Value, match.Groups["value"].Value);

        // Steam can leave a manifest truncated mid-write. No entries at all, or neither of the
        // two fields every real manifest carries, means this is not a manifest BEM can use.
        var buildId = Value(values, "buildid");
        var installDir = Value(values, "installdir");

        if (values.Count == 0 || (buildId.Length == 0 && installDir.Length == 0))
            return null;

        var stateFlags = Number(values, "StateFlags");

        return new SteamAppManifest(
            buildId,
            Value(values, "betakey") is { Length: > 0 } branch ? branch : PublicBranch,
            installDir,
            (stateFlags & UpdatePendingMask) != 0,
            Number(values, "AutoUpdateBehavior"));
    }

    private static string Value(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : string.Empty;

    private static int Number(Dictionary<string, string> values, string key) =>
        int.TryParse(Value(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
