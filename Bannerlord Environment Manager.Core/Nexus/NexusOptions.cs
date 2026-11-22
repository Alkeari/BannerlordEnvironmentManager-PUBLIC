using System.Text.Json;
using System.Text.Json.Serialization;

namespace BannerlordEnvironmentManager.Core.Nexus;

// On by default, and inert until the user signs in with Nexus, which is not the same thing as being
// off: nothing is sent anywhere without a key, and with a key this is one cached request per session.
// Switchable for anyone who wants BEM entirely offline.
//
// The startup check for a newer BEM needs no key and is on by default for the same reason: it is one
// small public request a day that sends nothing about the user.
public sealed record NexusOptions(
    bool UpdateCheckEnabled = true,
    NexusUpdatePeriod Period = NexusUpdatePeriod.Week,
    int CacheHours = 6,
    bool BemUpdateCheckAtStartup = true)
{
    public static NexusOptions Default { get; } = new();

    public TimeSpan CacheLifetime => TimeSpan.FromHours(Math.Clamp(CacheHours, 1, 168));
}

// Deliberately a separate file from BEM's general settings.json and from the credential: the flags
// are shareable, the key never is.
public sealed class NexusOptionsStore(string filePath)
{
    public const string FileName = "nexus-settings.json";

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string FilePath { get; } = filePath;

    public NexusOptions Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<NexusOptions>(File.ReadAllText(FilePath), Format) ?? NexusOptions.Default
                : NexusOptions.Default;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return NexusOptions.Default;
        }
    }

    public void Save(NexusOptions options)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(options, Format));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}
