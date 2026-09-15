namespace BannerlordEnvironmentManager.Core.GameSettings;

public sealed record GameSettingsExclusion(GameSettingsFileKind Kind, string Key, string Reason);

// The one place a settings key that is an instance's own truth rather than a player preference is
// written down, the way GameDlc.Known is the one place a DLC is. A key listed here is never
// projected onto an instance and never folded back into the shared file, so it stays whatever the
// instance itself last wrote. Adding one is a row here and nothing else.
public static class GameSettingsExclusions
{
    public static readonly IReadOnlyList<GameSettingsExclusion> All =
    [
        new GameSettingsExclusion(
            GameSettingsFileKind.EngineConfig,
            "safely_exited",
            "The crash-detection flag: the game clears it on launch and sets it on a clean exit, so "
            + "carrying another instance's value in either fakes a crash that never happened or hides "
            + "one that did."),
        new GameSettingsExclusion(
            GameSettingsFileKind.EngineConfig,
            "first_time",
            "The first-run flag, true only until an instance has been through its own first launch; "
            + "sharing it makes a fresh instance skip the setup it has not actually done."),
        new GameSettingsExclusion(
            GameSettingsFileKind.BannerlordConfig,
            "LatestSaveGameName",
            "Names the save the main menu offers to continue, and that save lives in one instance's own "
            + "Game Saves folder; another instance would offer a save it does not have.")
    ];

    private static readonly Dictionary<GameSettingsFileKind, HashSet<string>> ByKind = All
        .GroupBy(exclusion => exclusion.Kind)
        .ToDictionary(group => group.Key, group => group.Select(e => e.Key).ToHashSet(StringComparer.Ordinal));

    public static bool IsExcluded(GameSettingsFileKind kind, string key) =>
        ByKind.TryGetValue(kind, out var keys) && keys.Contains(key);

    public static IReadOnlyList<GameSettingsExclusion> For(GameSettingsFileKind kind) =>
        [.. All.Where(exclusion => exclusion.Kind == kind)];
}
