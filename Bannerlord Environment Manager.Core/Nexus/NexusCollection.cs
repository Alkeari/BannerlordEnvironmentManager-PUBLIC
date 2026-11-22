using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BannerlordEnvironmentManager.Core.Nexus;

// Which collection, and which revision of it. A null revision is the one the curator last published,
// which is what a collection page's own link points at when it names no revision.
public sealed partial record NexusCollectionRef(string Slug, int? Revision)
{
    public string PageUrl => Revision is { } revision
        ? $"https://www.nexusmods.com/games/{NexusEndpoints.GameDomain}/collections/{Slug}/revisions/{revision}"
        : $"https://www.nexusmods.com/games/{NexusEndpoints.GameDomain}/collections/{Slug}";

    // Accepts what a player has in hand: the page address from the browser (with or without a revision
    // or a tab after it), the nxm:// link a collection's download button sends, or the bare slug. A
    // collection for any other game is refused rather than read as Bannerlord's, since a slug is only
    // unique within its game.
    public static NexusCollectionRef? TryParse(string? text)
    {
        var value = text?.Trim();

        if (string.IsNullOrEmpty(value))
            return null;

        if (SlugPattern().IsMatch(value))
            return new NexusCollectionRef(value.ToLowerInvariant(), null);

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (uri.Scheme.Equals("nxm", StringComparison.OrdinalIgnoreCase))
            return uri.Host.Equals(NexusEndpoints.GameDomain, StringComparison.OrdinalIgnoreCase)
                ? FromSegments(segments, 0)
                : null;

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!uri.Host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase)
            && !uri.Host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var at = Array.FindIndex(segments, segment => segment.Equals("collections", StringComparison.OrdinalIgnoreCase));

        return at > 0 && segments[at - 1].Equals(NexusEndpoints.GameDomain, StringComparison.OrdinalIgnoreCase)
            ? FromSegments(segments, at)
            : null;
    }

    private static NexusCollectionRef? FromSegments(string[] segments, int collectionsAt)
    {
        if (segments.Length <= collectionsAt + 1
            || !segments[collectionsAt].Equals("collections", StringComparison.OrdinalIgnoreCase)
            || !SlugPattern().IsMatch(segments[collectionsAt + 1]))
            return null;

        var slug = segments[collectionsAt + 1].ToLowerInvariant();

        if (segments.Length > collectionsAt + 3
            && segments[collectionsAt + 2].Equals("revisions", StringComparison.OrdinalIgnoreCase))
        {
            var revision = segments[collectionsAt + 3];

            if (revision.Equals("latest", StringComparison.OrdinalIgnoreCase))
                return new NexusCollectionRef(slug, null);

            return int.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
                ? new NexusCollectionRef(slug, number)
                : null;
        }

        return new NexusCollectionRef(slug, null);
    }

    // Letters and digits only, which is every slug Nexus mints and also what keeps the slug safe to
    // place inside a GraphQL string literal.
    [GeneratedRegex("^[A-Za-z0-9]{4,32}$")]
    private static partial Regex SlugPattern();
}

// One file a collection revision pins: the exact file on the exact mod page, which is the whole point
// of a collection, and the version Nexus lists for that file. UpdatePolicy is the curator's word on
// whether that pin is exact ("exact"), a floor ("prefer") or only a starting point ("latest"); GroupId
// is the file's lineage on its page, which is how a newer upload of the same file is recognized.
// ResolvedFrom is the entry as the curator pinned it when NexusCollectionFiles.Resolve moved this one
// to a newer file, and null when this is the pinned file itself.
public sealed record NexusCollectionMod(
    int ModId,
    int FileId,
    string ModName,
    string? FileName,
    string? Version,
    bool Optional,
    string? UpdatePolicy = null,
    long? GroupId = null,
    string? Category = null,
    NexusCollectionMod? ResolvedFrom = null);

// Unlisted counts the entries Nexus returned with no file behind them, which is what a file that was
// deleted or hidden after the revision was published looks like. They are counted rather than dropped
// in silence, so a plan for 68 mods out of 69 says so.
public sealed record NexusCollectionRevision(
    string Slug,
    string Name,
    int RevisionNumber,
    IReadOnlyList<NexusCollectionMod> Mods,
    int Unlisted = 0,
    string? BundleLink = null,
    long BundleBytes = 0)
{
    public NexusCollectionRef Ref => new(Slug, RevisionNumber);
}

public enum NexusCollectionFailure
{
    None,
    NotFound,
    Unreadable,
    Refused,
    NoAnswer
}

// Exactly one of Revision or a failure. Detail carries Nexus's own words or the transport's, so a
// refusal is reported as what was said rather than as a code.
public sealed record NexusCollectionFetch(
    NexusCollectionRevision? Revision,
    NexusCollectionFailure Failure,
    int StatusCode = 0,
    string? Detail = null)
{
    public bool Found => Revision is not null;
}

// Nexus's v2 GraphQL API answers a collection revision's file list without any credential, so this
// works for every account, signed in or not. The revision's own bundle (the curator's settings and other
// bundled files) needs a signed-in download: the revision names its link and size here, and the app fetches it.
public static class NexusCollections
{
    private const string ModFields = "assetsSizeBytes downloadLink modFiles { fileId optional updatePolicy file { modId name version groupId category date mod { name } } }";

    public static string Query(NexusCollectionRef collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        var scope = $"domainName: \"{NexusEndpoints.GameDomain}\", viewAdultContent: true";

        return collection.Revision is { } revision
            ? $"{{ collectionRevision(slug: \"{collection.Slug}\", revision: {revision}, {scope}) {{ revisionNumber collection {{ name slug }} {ModFields} }} }}"
            : $"{{ collection(slug: \"{collection.Slug}\", {scope}) {{ name slug latestPublishedRevision {{ revisionNumber {ModFields} }} }} }}";
    }

    public static async Task<NexusCollectionFetch> FetchAsync(
        INexusPublicQuery nexus,
        NexusCollectionRef collection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nexus);
        ArgumentNullException.ThrowIfNull(collection);

        var response = await nexus.QueryAsync(Query(collection), cancellationToken);

        if (response.TransportError is { } error)
            return new NexusCollectionFetch(null, NexusCollectionFailure.NoAnswer, Detail: error);

        if (!response.IsSuccess)
            return new NexusCollectionFetch(null, NexusCollectionFailure.Refused, response.StatusCode);

        var fetch = Parse(response.Body);

        return fetch with { StatusCode = response.StatusCode };
    }

    public static NexusCollectionFetch Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new NexusCollectionFetch(null, NexusCollectionFailure.Unreadable);

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                return FromError(errors[0]);

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return new NexusCollectionFetch(null, NexusCollectionFailure.Unreadable);

            if (Object(data, "collectionRevision") is { } pinned)
                return Read(Object(pinned, "collection"), pinned);

            if (Object(data, "collection") is { } collection)
                return Object(collection, "latestPublishedRevision") is { } latest
                    ? Read(collection, latest)
                    : new NexusCollectionFetch(null, NexusCollectionFailure.NotFound);

            return new NexusCollectionFetch(null, NexusCollectionFailure.NotFound);
        }
        catch (JsonException)
        {
            return new NexusCollectionFetch(null, NexusCollectionFailure.Unreadable);
        }
    }

    private static NexusCollectionFetch FromError(JsonElement error)
    {
        var message = Text(error, "message");
        var code = Object(error, "extensions") is { } extensions ? Text(extensions, "code") : null;

        return new NexusCollectionFetch(
            null,
            string.Equals(code, "NOT_FOUND", StringComparison.OrdinalIgnoreCase)
                ? NexusCollectionFailure.NotFound
                : NexusCollectionFailure.Refused,
            Detail: message);
    }

    private static NexusCollectionFetch Read(JsonElement? collection, JsonElement revision)
    {
        if (collection is not { } owner
            || Text(owner, "slug") is not { Length: > 0 } slug
            || !revision.TryGetProperty("revisionNumber", out var number)
            || number.ValueKind != JsonValueKind.Number
            || !revision.TryGetProperty("modFiles", out var files)
            || files.ValueKind != JsonValueKind.Array)
            return new NexusCollectionFetch(null, NexusCollectionFailure.Unreadable);

        var mods = new List<NexusCollectionMod>();
        var unlisted = 0;

        foreach (var entry in files.EnumerateArray())
        {
            if (Object(entry, "file") is not { } file
                || !entry.TryGetProperty("fileId", out var fileId) || !fileId.TryGetInt32(out var fileNumber)
                || !file.TryGetProperty("modId", out var modId) || !modId.TryGetInt32(out var modNumber))
            {
                unlisted++;
                continue;
            }

            var fileName = Text(file, "name");
            var modName = Object(file, "mod") is { } page ? Text(page, "name") : null;

            mods.Add(new NexusCollectionMod(
                modNumber,
                fileNumber,
                modName is { Length: > 0 } ? modName : fileName ?? modNumber.ToString(CultureInfo.InvariantCulture),
                fileName,
                Text(file, "version"),
                entry.TryGetProperty("optional", out var optional) && optional.ValueKind == JsonValueKind.True,
                Text(entry, "updatePolicy"),
                file.TryGetProperty("groupId", out var groupId) && groupId.TryGetInt64(out var group) ? group : null,
                Text(file, "category")));
        }

        return new NexusCollectionFetch(
            new NexusCollectionRevision(slug, Text(owner, "name") ?? slug, number.GetInt32(), mods, unlisted,
                Text(revision, "downloadLink"),
                revision.TryGetProperty("assetsSizeBytes", out var assets) && long.TryParse(assets.ToString(), out var bytes) ? bytes : 0),
            NexusCollectionFailure.None);
    }

    internal static JsonElement? Object(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    internal static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public enum NexusCollectionStep
{
    Install,
    Update,
    AlreadyInstalled,
    // The copy on disk is ahead of the file the collection pins, or its version cannot be read against
    // it. Either way it is not written over on a guess.
    LeftAlone
}

public sealed record NexusCollectionItem(
    NexusCollectionMod Mod,
    NexusCollectionStep Step,
    string? InstalledModuleId = null,
    string? InstalledVersion = null)
{
    public bool NeedsFetch => Step is NexusCollectionStep.Install or NexusCollectionStep.Update;

    public ModUpdateTarget? Target => NeedsFetch
        ? new ModUpdateTarget(InstalledModuleId ?? string.Empty, Mod.ModName, Mod.ModId, Mod.FileId, Mod.FileName, Mod.Version)
        : null;
}

public sealed record NexusCollectionPlan(NexusCollectionRevision Revision, IReadOnlyList<NexusCollectionItem> Items)
{
    // Optional mods are the curator's suggestions, so they are fetched only when asked for.
    public IReadOnlyList<NexusCollectionItem> Fetches(bool includeOptional) =>
        [.. Items.Where(item => item.NeedsFetch && (includeOptional || !item.Mod.Optional))];

    public int OptionalCount => Items.Count(item => item.Mod.Optional);

    public int Count(NexusCollectionStep step) => Items.Count(item => item.Step == step);
}

public static class NexusCollectionPlanner
{
    // One instance's modules against one revision. A module counts as the collection's mod only by the
    // Nexus page BEM linked it to; a name match is not evidence, since two pages can carry the same
    // module and one page can carry several modules.
    public static NexusCollectionPlan Plan(NexusCollectionRevision revision, IEnumerable<NexusModuleLink> installed)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(installed);

        var byPage = installed
            .Where(link => link.NexusModId is not null)
            .GroupBy(link => link.NexusModId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        return new NexusCollectionPlan(
            revision,
            [.. revision.Mods.Select(mod => Decide(mod, byPage.GetValueOrDefault(mod.ModId)))]);
    }

    private static NexusCollectionItem Decide(NexusCollectionMod mod, List<NexusModuleLink>? onDisk)
    {
        if (onDisk is null || onDisk.Count == 0)
            return new NexusCollectionItem(mod, NexusCollectionStep.Install);

        if (onDisk.FirstOrDefault(link => link.NexusFileId == mod.FileId) is { } exact)
            return new NexusCollectionItem(mod, NexusCollectionStep.AlreadyInstalled, exact.ModuleId, VersionOf(exact));

        var link = onDisk[0];
        var local = VersionOf(link);

        if (local is not { Length: > 0 } || mod.Version is not { Length: > 0 } pinned)
            return new NexusCollectionItem(mod, NexusCollectionStep.LeftAlone, link.ModuleId, local);

        if (ModuleUpdateVerdicts.SameVersion(local, pinned))
            return new NexusCollectionItem(mod, NexusCollectionStep.AlreadyInstalled, link.ModuleId, local);

        return ModuleUpdateVerdicts.Compare(local, pinned) is ModuleVersionComparison.Behind
            ? new NexusCollectionItem(mod, NexusCollectionStep.Update, link.ModuleId, local)
            : new NexusCollectionItem(mod, NexusCollectionStep.LeftAlone, link.ModuleId, local);
    }

    // Nexus's own record of the installed file is compared before the module's SubModule.xml, because
    // the collection's version is Nexus's number for the file and the manifest's is the author's habit.
    private static string? VersionOf(NexusModuleLink link) =>
        link.NexusVersionAtInstall is { Length: > 0 } atInstall ? atInstall : link.InstalledVersion;
}
