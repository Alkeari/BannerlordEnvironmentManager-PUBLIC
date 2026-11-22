namespace BannerlordEnvironmentManager.Core.GameSettings;

// Whether the base copy in an instance's own-game-settings folder is the player's file or BEM's, and
// with it the whole of what turning sharing off means for that file.
//
// What sharing stores is the set an instance was on WITHOUT the toggle, kept for the moment the
// toggle goes back off. None is the state where such a set exists, and only there is anything put
// back. For a seeded instance there is no such set and never was: the instance had no settings of
// its own, so its own settings are the ones the game writes from its defaults, and that is what a
// restore leaves it to do. Nothing the player can name is lost, because whatever they chose while
// sharing was on is in the shared file.
//
// Seed and Grown therefore behave identically on the way out, and that is the point rather than an
// oversight. A grown file carries the build's own key set, but every value in it came from sharing,
// so treating it as the instance's own would make the toggle mean one thing for an instance that
// existed before sharing and another for one that did not. Two meanings decided by an instance's
// history is worse than either answer taken consistently.
public enum GameSettingsSeedState
{
    // The base copy is the instance's own file, frozen the first time sharing touched it. The one
    // state a restore puts something back in.
    None,

    // BEM wrote the file, because the instance had none. The game has not run against it yet, so the
    // base copy is still the seed itself. A restore deletes it.
    Seed,

    // BEM wrote the file, the game has since run and written its own, and the capture after that run
    // replaced the base copy with it. Still not the player's own file: the instance never had one,
    // and a restore deletes it exactly as it deletes a seed.
    Grown
}

// The marker that says a base copy was never the player's, written beside the copy it describes.
//
// It exists because a seed must not become a frozen baseline. OwnGameSettings freezes a file so the
// toggle is honestly reversible: the bytes the player had are on disk and go back when sharing is
// turned off. A seeded instance had no such bytes, so freezing the seed would record the shared
// values as that instance's own and turning sharing off would leave it on them forever, which is the
// opposite of what the toggle promises.
//
// A file per kind rather than one per instance, for the same reason OwnGameSettings freezes per
// kind: an instance can be seeded for engine_config.txt and never for BannerlordGameKeys.xml, which
// is the ordinary case, since BEM cannot compose a hotkey file at all.
public static class SeededGameSettings
{
    public const string MarkerExtension = ".seeded";

    public static string MarkerPath(string instanceFolder, GameSettingsFileKind kind) =>
        OwnGameSettings.FrozenPath(instanceFolder, kind) + MarkerExtension;

    // A marker BEM cannot read still says BEM wrote the file, which is the fact that matters: the
    // only thing lost is whether the run has happened yet, and reading it as the earlier state costs
    // one more base refresh rather than a file put back as something it never was.
    public static GameSettingsSeedState StateOf(string instanceFolder, GameSettingsFileKind kind)
    {
        var path = MarkerPath(instanceFolder, kind);

        try
        {
            if (!File.Exists(path))
                return GameSettingsSeedState.None;

            return Enum.TryParse<GameSettingsSeedState>(File.ReadAllText(path).Trim(), out var state)
                   && state != GameSettingsSeedState.None
                ? state
                : GameSettingsSeedState.Seed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GameSettingsSeedState.Seed;
        }
    }

    public static void Mark(string instanceFolder, GameSettingsFileKind kind, GameSettingsSeedState state)
    {
        if (state == GameSettingsSeedState.None)
        {
            Clear(instanceFolder, kind);
            return;
        }

        var path = MarkerPath(instanceFolder, kind);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, state.ToString());
    }

    public static void Clear(string instanceFolder, GameSettingsFileKind kind)
    {
        var path = MarkerPath(instanceFolder, kind);

        if (File.Exists(path))
            File.Delete(path);
    }
}
