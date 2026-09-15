namespace BannerlordEnvironmentManager.Core.Launcher;

public enum LaunchTargetKind
{
    BlseStandalone,
    GameExecutable,
    Steam,
    TaleWorldsLauncher,
    BlseLauncher,
    BlseLauncherEx
}

public sealed record LaunchTarget(
    LaunchTargetKind Kind,
    string Path,
    string Arguments,
    string DisplayName)
{
    public bool IsUri => Kind == LaunchTargetKind.Steam;

    // A dropdown item with no automation name of its own is announced by its ToString, and a record's
    // own ToString reads the type name and every member aloud.
    public override string ToString() => DisplayName;
}

public static class LaunchTargetPreference
{
    // Settings written before the launcher dropdown stored a positional index, so adding a kind to
    // the enum would have silently repointed a saved choice at a different target. The preference is
    // stored by name now; this mapping is the old index order and must never be renumbered.
    public static LaunchTargetKind? Read(string? name, int? legacyIndex)
    {
        foreach (var kind in Enum.GetValues<LaunchTargetKind>())
        {
            if (string.Equals(kind.ToString(), name, StringComparison.OrdinalIgnoreCase))
                return kind;
        }

        return legacyIndex switch
        {
            1 => LaunchTargetKind.BlseStandalone,
            2 => LaunchTargetKind.GameExecutable,
            3 => LaunchTargetKind.Steam,
            4 => LaunchTargetKind.TaleWorldsLauncher,
            _ => null
        };
    }
}
