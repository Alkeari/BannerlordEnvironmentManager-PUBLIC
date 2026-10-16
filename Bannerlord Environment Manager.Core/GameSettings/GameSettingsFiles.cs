namespace BannerlordEnvironmentManager.Core.GameSettings;

// The base game's own settings, and only those three files. Everything else under an instance's
// Configs folder is deliberately absent: ModSettings is MCM's, LauncherData.xml is the load order
// BEM already owns per instance, ModLogs and Game Saves are one run's record, and a mod-named
// folder such as CulturedStartReloaded belongs to that mod. Sharing any of them would carry one
// instance's mods into another that does not have them.
public enum GameSettingsFileKind
{
    EngineConfig,
    BannerlordConfig,
    GameKeys
}

// How the game itself lays out a settings file, for the one case where BEM has to write a file the
// game has never written: what sits around the equals sign, what ends a line, whether the last line
// is ended at all, and whether the file opens with a byte order mark. Read off the real installs on
// this machine rather than assumed, and asserted against the shipped fixtures.
public sealed record GameSettingsFileFormat(
    string Separator,
    string LineEnding,
    bool TrailingLineEnding,
    bool HasByteOrderMark);

public static class GameSettingsFiles
{
    public const string EngineConfigFileName = "engine_config.txt";
    public const string BannerlordConfigFileName = "BannerlordConfig.txt";
    public const string GameKeysFileName = "BannerlordGameKeys.xml";

    public static readonly IReadOnlyList<GameSettingsFileKind> All =
    [
        GameSettingsFileKind.EngineConfig,
        GameSettingsFileKind.BannerlordConfig,
        GameSettingsFileKind.GameKeys
    ];

    public static string NameOf(GameSettingsFileKind kind) => kind switch
    {
        GameSettingsFileKind.EngineConfig => EngineConfigFileName,
        GameSettingsFileKind.BannerlordConfig => BannerlordConfigFileName,
        GameSettingsFileKind.GameKeys => GameKeysFileName,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    // Null for BannerlordGameKeys.xml, and that is the whole of why hotkeys are never written into an
    // instance that has no file for them. A key-value file is completely described by its keys, its
    // values and the four facts above, so BEM can write one. A hotkey file is not: the shared set
    // holds "<category>/<id>" against the inner XML of a Keys element and nothing else, while the
    // file also carries which of GameKey, GameAxisKey and HotKey each entry is, the root's own
    // version attribute, and the order the categories come in. Worse, writing one at all would mean
    // creating every category the shared set carries, including the six a mod owns on the resting
    // install, which is exactly what GameSettingsMerge refuses to do. So an instance with no hotkey
    // file keeps the game's own bindings on its first run and joins the shared set at the capture
    // after it.
    public static GameSettingsFileFormat? FormatOf(GameSettingsFileKind kind) => kind switch
    {
        GameSettingsFileKind.EngineConfig => new GameSettingsFileFormat(
            Separator: " = ", LineEnding: "\r\n", TrailingLineEnding: true, HasByteOrderMark: false),
        GameSettingsFileKind.BannerlordConfig => new GameSettingsFileFormat(
            Separator: "=", LineEnding: "\n", TrailingLineEnding: false, HasByteOrderMark: true),
        GameSettingsFileKind.GameKeys => null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static IGameSettingsDocument Parse(GameSettingsFileKind kind, ConfigText text) => kind switch
    {
        GameSettingsFileKind.GameKeys => GameKeysDocument.Parse(text),
        _ => KeyValueConfigDocument.Parse(text)
    };

    public static IGameSettingsDocument Read(GameSettingsFileKind kind, string path) =>
        Parse(kind, ConfigText.Read(path));
}
