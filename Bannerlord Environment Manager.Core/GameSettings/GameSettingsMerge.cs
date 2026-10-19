namespace BannerlordEnvironmentManager.Core.GameSettings;

public sealed record GameSettingsCapture(IReadOnlyDictionary<string, string> Shared, int Added, int Updated);

// What the projection actually wrote, key by key, and how many of those were a change to the file.
// Produced here because the intersection rule and the GameSettingsExclusions skip both live in the
// loop below, so a key that was never applied cannot end up in it.
public sealed record GameSettingsProjection(IReadOnlyDictionary<string, string> Applied, int Changed);

// The merge, and the only place the rules are written down.
//
// Projection is an intersection, never a union. A key the shared file has and the instance does not
// is left out, which is what stops a setting a newer build introduced from reaching an older one:
// 1.5.2 declares keep_shader_cache_on_module_change and four Completed*Playthrough keys that 1.4.7
// has never heard of. For BannerlordGameKeys.xml the same rule reads as "never create a category":
// the resting install carries 30 hotkey categories against 24 in a managed instance, and the six
// extras (ArenaOverhaul, BetterSmithingContinued, CheyronMod, HoldCourt, TroopSortHotkeyCategory and
// sae_keybinding) are owned by mods that instance does not have.
//
// Capture is the other direction, and a union: a key the shared file lacks is added, which is how it
// grows into the superset across versions, and a key whose value differs is taken as the player's
// edit. Which runs are captured at all is GameSettingsSync's decision, not this one's.
public static class GameSettingsMerge
{
    // The document is the file the instance has now, so a key only it has keeps its own value by
    // simply never being visited. That is what makes projecting onto the live file safe: the mod
    // category and the newer build's key are not in the shared set, so the loop does not reach them.
    public static GameSettingsProjection Project(
        IGameSettingsDocument ownBase,
        GameSettingsFileKind kind,
        IReadOnlyDictionary<string, string> shared)
    {
        ArgumentNullException.ThrowIfNull(ownBase);
        ArgumentNullException.ThrowIfNull(shared);

        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        var changed = 0;

        foreach (var key in ownBase.Values.Keys.ToList())
        {
            if (GameSettingsExclusions.IsExcluded(kind, key) || !shared.TryGetValue(key, out var value))
                continue;

            changed += ownBase.Set(key, value);
            applied[key] = value;
        }

        return new GameSettingsProjection(applied, changed);
    }

    public static GameSettingsCapture Capture(
        IReadOnlyDictionary<string, string> shared,
        IGameSettingsDocument observed,
        GameSettingsFileKind kind)
    {
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(observed);

        var result = new Dictionary<string, string>(shared, StringComparer.Ordinal);
        var added = 0;
        var updated = 0;

        foreach (var (key, value) in observed.Values)
        {
            if (GameSettingsExclusions.IsExcluded(kind, key))
                continue;

            if (!result.TryGetValue(key, out var existing))
                added++;
            else if (existing != value)
                updated++;
            else
                continue;

            result[key] = value;
        }

        return new GameSettingsCapture(result, added, updated);
    }
}
