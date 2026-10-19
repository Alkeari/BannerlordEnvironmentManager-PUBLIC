using System.Text.Json;
using System.Text.Json.Serialization;

namespace BannerlordEnvironmentManager.Core.GameSettings;

// The one set of values every instance draws from while sharing is on. It is BEM's own file rather
// than a fourth copy of the game's, so it holds keys and values and nothing about line endings,
// duplicate lines or element order: the instance's own frozen file supplies all of that, and this
// supplies only what to put in it.
//
// The three maps are named properties rather than a map keyed on the enum so the file stays readable
// and a settings file written before a kind existed still deserializes.
public sealed record SharedGameSettings
{
    public static SharedGameSettings Empty { get; } = new();

    public Dictionary<string, string> EngineConfig { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> BannerlordConfig { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> GameKeys { get; init; } = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> For(GameSettingsFileKind kind) => kind switch
    {
        GameSettingsFileKind.EngineConfig => EngineConfig,
        GameSettingsFileKind.BannerlordConfig => BannerlordConfig,
        GameSettingsFileKind.GameKeys => GameKeys,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    [JsonIgnore]
    public bool IsEmpty => GameSettingsFiles.All.All(kind => For(kind).Count == 0);

    public SharedGameSettings With(GameSettingsFileKind kind, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var replacement = new Dictionary<string, string>(values, StringComparer.Ordinal);

        return kind switch
        {
            GameSettingsFileKind.EngineConfig => this with { EngineConfig = replacement },
            GameSettingsFileKind.BannerlordConfig => this with { BannerlordConfig = replacement },
            GameSettingsFileKind.GameKeys => this with { GameKeys = replacement },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }
}

// Beside instances.json, so no instance owns the shared file and removing an instance cannot take it
// away. The path is supplied rather than read from the environment: Core does not decide where BEM's
// app data lives, and a test needs to point this at a temporary folder.
public sealed class SharedGameSettingsStore(string filePath)
{
    public const string FileName = "shared-game-settings.json";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string FilePath { get; } = filePath ?? throw new ArgumentNullException(nameof(filePath));

    public static SharedGameSettingsStore Beside(string instanceSettingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceSettingsPath);

        var directory = Path.GetDirectoryName(instanceSettingsPath);

        return new SharedGameSettingsStore(Path.Combine(directory ?? string.Empty, FileName));
    }

    // A shared file that cannot be read reads as empty, and an empty shared file projects nothing at
    // all: every key is left at the instance's own value. Sharing failing closed is the only safe
    // direction, since the alternative is writing a half-read set over a working config.
    public SharedGameSettings Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<SharedGameSettings>(File.ReadAllText(FilePath), Format)
                  ?? SharedGameSettings.Empty
                : SharedGameSettings.Empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return SharedGameSettings.Empty;
        }
    }

    public void Write(SharedGameSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Format));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
