using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BannerlordEnvironmentManager.Core.Nexus;

// One file on a mod page as Nexus lists it now. Date is the upload time in Unix seconds.
public sealed record NexusCollectionFile(int FileId, string? Name, string? Version, string? Category, long Date, long? GroupId);

// Exactly one of Files or a failure, as with NexusCollectionFetch. A mod Nexus answered nothing for is
// absent from Files, which leaves its entries on their pinned files.
public sealed record NexusModFilesFetch(
    IReadOnlyDictionary<int, IReadOnlyList<NexusCollectionFile>>? Files,
    NexusCollectionFailure Failure,
    int StatusCode = 0,
    string? Detail = null)
{
    public bool Found => Files is not null;
}

// The revision a read plans against, and the failed file fetch when the curator's choice could not be
// followed, so the player is told why every entry stayed on its pin.
public sealed record NexusCollectionResolution(NexusCollectionRevision Revision, NexusModFilesFetch? Unread);

// A collection pins files, but each entry also carries the curator's update policy, and Vortex installs
// by that policy rather than by the pin (transformCollection.ts and checkModsVersion.ts, Vortex
// 2e29b16): "latest" takes the newest current file of the same lineage, "prefer" takes it only when
// its version is not below the pin, and "exact" or no policy keeps the pin. A collection published
// before a mod's newer upload would otherwise install the very file the author since retired.
public static class NexusCollectionFiles
{
    // Categories Nexus uses for a file its author has replaced or withdrawn; none of them is a current
    // file, so none is ever chosen over the pin.
    private static readonly HashSet<string> Retired = new(StringComparer.OrdinalIgnoreCase) { "OLD_VERSION", "ARCHIVED", "REMOVED" };

    public static IReadOnlyList<int> ModsToCheck(NexusCollectionRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);

        return [.. revision.Mods.Where(FollowsUpdates).Select(mod => mod.ModId).Distinct().Order()];
    }

    // Every mod in one request, each under its own alias, so a 69-mod collection costs one call.
    public static string Query(IReadOnlyList<int> modIds)
    {
        ArgumentNullException.ThrowIfNull(modIds);

        var query = new StringBuilder("{ ");

        for (var i = 0; i < modIds.Count; i++)
            query.Append(CultureInfo.InvariantCulture,
                $"m{i}: modFiles(modId: \"{modIds[i]}\", gameId: \"{BemUpdateCheck.NexusGameId}\") {{ fileId name version category date groupId }} ");

        return query.Append('}').ToString();
    }

    public static async Task<NexusModFilesFetch> FetchAsync(
        INexusPublicQuery nexus,
        NexusCollectionRevision revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nexus);

        var modIds = ModsToCheck(revision);

        if (modIds.Count == 0)
            return new NexusModFilesFetch(new Dictionary<int, IReadOnlyList<NexusCollectionFile>>(), NexusCollectionFailure.None);

        var response = await nexus.QueryAsync(Query(modIds), cancellationToken);

        if (response.TransportError is { } error)
            return new NexusModFilesFetch(null, NexusCollectionFailure.NoAnswer, Detail: error);

        if (!response.IsSuccess)
            return new NexusModFilesFetch(null, NexusCollectionFailure.Refused, response.StatusCode);

        return Parse(response.Body, modIds) with { StatusCode = response.StatusCode };
    }

    public static async Task<NexusCollectionResolution> ApplyAsync(
        INexusPublicQuery nexus,
        NexusCollectionRevision revision,
        bool followCurator,
        CancellationToken cancellationToken)
    {
        if (!followCurator)
            return new NexusCollectionResolution(revision, null);

        var fetch = await FetchAsync(nexus, revision, cancellationToken);

        return fetch.Files is { } files
            ? new NexusCollectionResolution(Resolve(revision, files), null)
            : new NexusCollectionResolution(revision, fetch);
    }

    // An error beside data means some aliases failed (a mod removed or hidden) and the rest answered,
    // so the answered ones are kept; only an error with no data at all fails the fetch.
    public static NexusModFilesFetch Parse(string? body, IReadOnlyList<int> modIds)
    {
        ArgumentNullException.ThrowIfNull(modIds);

        if (string.IsNullOrWhiteSpace(body))
            return new NexusModFilesFetch(null, NexusCollectionFailure.Unreadable);

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (NexusCollections.Object(root, "data") is not { } data)
                return root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
                    ? new NexusModFilesFetch(null, NexusCollectionFailure.Refused, Detail: NexusCollections.Text(errors[0], "message"))
                    : new NexusModFilesFetch(null, NexusCollectionFailure.Unreadable);

            var files = new Dictionary<int, IReadOnlyList<NexusCollectionFile>>();

            for (var i = 0; i < modIds.Count; i++)
            {
                if (!data.TryGetProperty($"m{i}", out var list) || list.ValueKind != JsonValueKind.Array)
                    continue;

                files[modIds[i]] = [.. list.EnumerateArray().Select(Read).OfType<NexusCollectionFile>()];
            }

            return new NexusModFilesFetch(files, NexusCollectionFailure.None);
        }
        catch (JsonException)
        {
            return new NexusModFilesFetch(null, NexusCollectionFailure.Unreadable);
        }
    }

    // The revision with each entry on the file its policy picks. A mod with no file list, or no current
    // file in the pinned file's lineage (a page whose every file is archived), keeps its pin.
    public static NexusCollectionRevision Resolve(NexusCollectionRevision revision, IReadOnlyDictionary<int, IReadOnlyList<NexusCollectionFile>> files)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(files);

        return revision with { Mods = [.. revision.Mods.Select(mod => Resolve(mod, files.GetValueOrDefault(mod.ModId)))] };
    }

    private static NexusCollectionMod Resolve(NexusCollectionMod mod, IReadOnlyList<NexusCollectionFile>? onPage)
    {
        if (!FollowsUpdates(mod) || onPage is null)
            return mod;

        var newest = onPage
            .Where(file => file.GroupId == mod.GroupId && file.Category is { } category && !Retired.Contains(category))
            .OrderByDescending(file => file.Date)
            .ThenByDescending(file => file.FileId)
            .FirstOrDefault();

        if (newest is null || newest.FileId == mod.FileId)
            return mod;

        if (IsPolicy(mod, "prefer")
            && ModuleUpdateVerdicts.Compare(mod.Version, newest.Version) is not (ModuleVersionComparison.Behind or ModuleVersionComparison.Same))
            return mod;

        return mod with
        {
            FileId = newest.FileId,
            FileName = newest.Name,
            Version = newest.Version,
            Category = newest.Category,
            ResolvedFrom = mod
        };
    }

    private static bool FollowsUpdates(NexusCollectionMod mod) =>
        mod.GroupId is not null && (IsPolicy(mod, "latest") || IsPolicy(mod, "prefer"));

    private static bool IsPolicy(NexusCollectionMod mod, string policy) =>
        string.Equals(mod.UpdatePolicy, policy, StringComparison.OrdinalIgnoreCase);

    private static NexusCollectionFile? Read(JsonElement file)
    {
        if (file.ValueKind != JsonValueKind.Object
            || !file.TryGetProperty("fileId", out var fileId) || !fileId.TryGetInt32(out var id)
            || !file.TryGetProperty("date", out var date) || !date.TryGetInt64(out var uploaded))
            return null;

        return new NexusCollectionFile(
            id,
            NexusCollections.Text(file, "name"),
            NexusCollections.Text(file, "version"),
            NexusCollections.Text(file, "category"),
            uploaded,
            file.TryGetProperty("groupId", out var groupId) && groupId.TryGetInt64(out var group) ? group : null);
    }
}
