using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Safety;

public enum SafetyBlocklistSource
{
    // Never fetched and no cache on disk. Heuristics still run; only the confirmed-report match is off.
    None,
    Network,
    Cache,
    Disabled
}

public sealed record SafetyBlocklistEntry(
    string? Name,
    string? WorkshopId,
    IReadOnlyList<string> Sha256,
    string? Reason,
    string? Date);

// Unavailable is the reason the network read did not happen or did not work. It is said once, where
// the user would have used it, and never as an error that gates anything else.
public sealed record SafetyBlocklistState(
    SafetyBlocklistSource Source,
    int EntryCount,
    string Updated,
    string? Unavailable = null)
{
    public static SafetyBlocklistState Off { get; } = new(SafetyBlocklistSource.Disabled, 0, string.Empty);

    // An empty list that was read and a list that could not be read are opposite statements, and the
    // published list is empty today, so the first one is the sentence most users will see. It says the
    // list was read and names nobody; only a failed read says the list was not read.
    public string Describe() => Source switch
    {
        SafetyBlocklistSource.Network => EntryCount == 0
            ? Strings.Current["Core.Safety.Blocklist.Network.Empty"]
            : Strings.Current.Plural("Core.Safety.Blocklist.Network.Count", EntryCount),
        SafetyBlocklistSource.Cache => (EntryCount == 0
            ? Strings.Current["Core.Safety.Blocklist.Cache.Empty"]
            : Strings.Current.Plural("Core.Safety.Blocklist.Cache.Count", EntryCount))
            + (Unavailable is null ? string.Empty : $" {Unavailable}"),
        SafetyBlocklistSource.Disabled => Strings.Current["Core.Safety.Blocklist.Disabled"],
        _ => Strings.Current["Core.Safety.Blocklist.NotRead"]
            + (Unavailable is null ? string.Empty : $" {Unavailable}")
    };
}

// Matched by Steam Workshop id or by the exact lowercase SHA-256 of a file, and NEVER by name. The
// rule is the whole point: a name match would let a coincidence of naming accuse an innocent author.
public sealed class SafetyBlocklistIndex
{
    private readonly Dictionary<string, SafetyBlocklistEntry> byWorkshopId;
    private readonly Dictionary<string, SafetyBlocklistEntry> byHash;

    public static SafetyBlocklistIndex Empty { get; } = new([]);

    public SafetyBlocklistIndex(IReadOnlyList<SafetyBlocklistEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        byWorkshopId = new Dictionary<string, SafetyBlocklistEntry>(StringComparer.Ordinal);
        byHash = new Dictionary<string, SafetyBlocklistEntry>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.WorkshopId))
                byWorkshopId[entry.WorkshopId.Trim()] = entry;

            foreach (var hash in entry.Sha256)
            {
                if (!string.IsNullOrWhiteSpace(hash))
                    byHash[hash.Trim().ToLowerInvariant()] = entry;
            }
        }
    }

    public bool MatchesByHash => byHash.Count > 0;

    public SafetyBlocklistEntry? ByWorkshopId(string? workshopId) =>
        string.IsNullOrWhiteSpace(workshopId) ? null : byWorkshopId.GetValueOrDefault(workshopId.Trim());

    public SafetyBlocklistEntry? ByHash(string? sha256) =>
        string.IsNullOrWhiteSpace(sha256) ? null : byHash.GetValueOrDefault(sha256.Trim().ToLowerInvariant());
}

public sealed record SafetyBlocklistDocument(
    [property: JsonPropertyName("updated")] string? Updated,
    [property: JsonPropertyName("entries")] IReadOnlyList<SafetyBlocklistJsonEntry>? Entries);

public sealed record SafetyBlocklistJsonEntry(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("workshopId")] string? WorkshopId,
    [property: JsonPropertyName("sha256")] IReadOnlyList<string>? Sha256,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("date")] string? Date);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SafetyBlocklistDocument))]
public sealed partial class SafetyBlocklistJsonContext : JsonSerializerContext
{
}

public delegate Task<string?> SafetyBlocklistFetch(CancellationToken cancellationToken);

// Fetch, cache, and fall back to the cache. Public data with no credential in it, so the cache is
// plain JSON. It is written to its own file and read by nothing else: no path here goes near the
// folder the Nexus key lives in beyond sharing BEM's own settings root, and nothing here ever writes
// a file whose name could collide with a credential file.
public sealed class SafetyBlocklistStore(string filePath)
{
    public const string FileName = "mod-safety-blocklist.json";

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        FileName);

    public string FilePath { get; } = filePath;

    public async Task<(SafetyBlocklistIndex Index, SafetyBlocklistState State)> LoadAsync(
        bool allowNetwork,
        SafetyBlocklistFetch? fetch,
        CancellationToken cancellationToken)
    {
        string? failure = null;

        if (!allowNetwork)
        {
            var (offlineEntries, offlineUpdated) = ReadCache();

            return offlineEntries is null
                ? (SafetyBlocklistIndex.Empty, SafetyBlocklistState.Off)
                : (new SafetyBlocklistIndex(offlineEntries),
                    new SafetyBlocklistState(SafetyBlocklistSource.Cache, offlineEntries.Count, offlineUpdated,
                        Strings.Current["Core.Safety.Blocklist.UpdatesOff"]));
        }

        if (fetch is not null)
        {
            try
            {
                var json = await fetch(cancellationToken);

                if (json is not null && Parse(json) is ({ } entries, var updated))
                {
                    Write(json);

                    return (new SafetyBlocklistIndex(entries),
                        new SafetyBlocklistState(SafetyBlocklistSource.Network, entries.Count, updated));
                }

                // Reached the server and got something that is not the list, which is a different
                // failure from not reaching it at all and reads differently to whoever is deciding
                // whether to trust the scan.
                failure = json is null
                    ? Strings.Current["Core.Safety.Blocklist.Fetch.Empty"]
                    : Strings.Current["Core.Safety.Blocklist.Fetch.WrongFormat"];
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // Any transport failure at all: the blocklist is optional and must never be able to stop
            // a scan, whatever the reason it could not be read.
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                failure = Strings.Current.Format("Core.Safety.Blocklist.Fetch.Failed", ex.Message);
            }
        }

        var (cached, cachedUpdated) = ReadCache();

        // The fallback is named only when it actually happened. Saying "the cached copy was used" on a
        // machine that has no cached copy is the same class of defect as calling a failed fetch an
        // empty list.
        return cached is null
            ? (SafetyBlocklistIndex.Empty,
                new SafetyBlocklistState(SafetyBlocklistSource.None, 0, string.Empty,
                    failure is null
                        ? Strings.Current["Core.Safety.Blocklist.NeverFetched"]
                        : Strings.Current.Format("Core.Safety.Blocklist.NoCache", failure)))
            : (new SafetyBlocklistIndex(cached),
                new SafetyBlocklistState(SafetyBlocklistSource.Cache, cached.Count, cachedUpdated,
                    failure is null ? null : Strings.Current.Format("Core.Safety.Blocklist.UsedCache", failure)));
    }

    public static (IReadOnlyList<SafetyBlocklistEntry>? Entries, string Updated) Parse(string json)
    {
        try
        {
            var document = JsonSerializer.Deserialize(json, SafetyBlocklistJsonContext.Default.SafetyBlocklistDocument);

            if (document is null)
                return (null, string.Empty);

            var entries = (document.Entries ?? [])
                .Select(entry => new SafetyBlocklistEntry(
                    entry.Name,
                    entry.WorkshopId,
                    entry.Sha256 ?? [],
                    entry.Reason,
                    entry.Date))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.WorkshopId) || entry.Sha256.Count > 0)
                .ToList();

            return (entries, document.Updated ?? string.Empty);
        }
        catch (JsonException)
        {
            return (null, string.Empty);
        }
    }

    private (IReadOnlyList<SafetyBlocklistEntry>? Entries, string Updated) ReadCache()
    {
        try
        {
            return File.Exists(FilePath) ? Parse(File.ReadAllText(FilePath)) : (null, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, string.Empty);
        }
    }

    private void Write(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
        }
    }
}
