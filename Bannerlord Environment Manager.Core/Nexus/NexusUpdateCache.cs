using System.Text.Json;
using System.Text.Json.Serialization;

namespace BannerlordEnvironmentManager.Core.Nexus;

// Null Version means the lookup did not answer, which is a different statement from a mod that has no
// version, and the reader is told so.
public sealed record NexusModVersion(int ModId, string? Version);

public sealed record NexusCachedFeed(
    DateTimeOffset FetchedUtc,
    NexusUpdatePeriod Period,
    IReadOnlyList<NexusUpdatedMod> Mods,
    IReadOnlyList<NexusModVersion>? Versions = null)
{
    public bool IsFreshAt(DateTimeOffset now, TimeSpan lifetime) =>
        now >= FetchedUtc && now - FetchedUtc < lifetime;
}

// A second look inside the cache window costs no request, and every answer carries the moment it was
// fetched so a stale answer can say it is stale rather than pass as current. Nothing here holds a
// credential: it is a list of public mod ids and timestamps.
public sealed class NexusUpdateCache(string filePath)
{
    public const string FileName = "nexus-update-cache.json";

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string FilePath { get; } = filePath;

    public NexusCachedFeed? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<NexusCachedFeed>(File.ReadAllText(FilePath), Format)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(NexusCachedFeed feed)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(feed, Format));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
