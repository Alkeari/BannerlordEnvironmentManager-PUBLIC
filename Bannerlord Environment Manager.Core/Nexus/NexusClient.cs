using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

public enum NexusOutcome
{
    Ok,
    Unauthorized,
    RateLimited,
    Unreachable,
    ServiceError,
    Malformed,
    Canceled,
    // Nexus answered and said it holds no such thing. A different statement from a service error, and
    // for a hash lookup it is a useful answer rather than a failure.
    NotFound
}

public sealed record NexusResult<T>(NexusOutcome Outcome, T? Value, string Message, NexusRateLimit RateLimit)
{
    public bool IsOk => Outcome == NexusOutcome.Ok;
}

public sealed record NexusUser(int UserId, string Name, bool IsPremium, bool IsSupporter);

public sealed record NexusUpdatedMod(int ModId, DateTimeOffset LatestFileUpdate, DateTimeOffset LatestModActivity);

public sealed record NexusDownloadUrl(string Name, string Uri);

// What Nexus says a set of bytes is. Every field is nullable because the answer is a mod record and a
// file record joined, and either half can omit a field; an absent version is "Nexus did not say",
// never "no version".
public sealed record NexusFileIdentity(
    int ModId,
    int? FileId,
    string? FileName,
    string? Version,
    DateTimeOffset? UploadedUtc);

public sealed record NexusModDetail(
    int ModId,
    string Name,
    string? Version,
    DateTimeOffset? UpdatedUtc,
    bool Available,
    bool ContainsAdultContent);

// Where Nexus files one particular file today. Nexus moves a file it has superseded into its old
// versions category, and that is the only statement it publishes about whether a single file is still
// the one to have. The version on the mod page tracks the main file and says nothing about an optional
// file, a patch or an MCM support file, which is how BEM came to call current files out of date.
public enum NexusFileStanding
{
    // Nexus stated a category BEM does not recognize, or stated none at all. Not a statement either
    // way, and deliberately not read as one.
    Unstated,
    Current,
    OldVersion
}

// Nexus publishes both a name and a number for a file's category. The name is read first because that
// is the form Nexus's own documentation writes down, and an unrecognized value is reported as unstated
// rather than guessed at: guessing here either accuses a current file or clears a superseded one.
public static class NexusFileCategories
{
    public const string OldVersion = "OLD_VERSION";

    private const string Main = "MAIN";

    private const int MainId = 1;

    private const int OldVersionId = 4;

    private static readonly string[] CurrentNames = ["MAIN", "UPDATE", "OPTIONAL", "MISCELLANEOUS"];

    private static readonly int[] CurrentIds = [1, 2, 3, 5];

    // The file an author has nominated as the one to take. Read the same way as the standing above:
    // the published name first, the number only where the name says nothing.
    public static bool IsMain(string? categoryName, int? categoryId)
    {
        var name = Normalize(categoryName);

        return name.Length > 0 ? name == Main : categoryId == MainId;
    }

    public static NexusFileStanding Read(string? categoryName, int? categoryId)
    {
        var name = Normalize(categoryName);

        if (name == OldVersion)
            return NexusFileStanding.OldVersion;

        if (CurrentNames.Contains(name))
            return NexusFileStanding.Current;

        return categoryId switch
        {
            OldVersionId => NexusFileStanding.OldVersion,
            { } id when CurrentIds.Contains(id) => NexusFileStanding.Current,
            _ => NexusFileStanding.Unstated
        };
    }

    private static string Normalize(string? categoryName) =>
        (categoryName ?? string.Empty).Trim().Replace(' ', '_').ToUpperInvariant();
}

// One file on a mod page. Name is the title the author gave the file, which is what a download keeps
// after BEM renames the archive; FileName is what Nexus calls the bytes.
public sealed record NexusModFile(
    int FileId,
    string? Name,
    string? FileName,
    string? Version,
    int? CategoryId,
    string? CategoryName,
    DateTimeOffset? UploadedUtc)
{
    [JsonIgnore]
    public NexusFileStanding Standing => NexusFileCategories.Read(CategoryName, CategoryId);
}

// Nexus's own record of which file replaced which, published beside the file list. It is what lets an
// out-of-date verdict name the file to go and get rather than only assert that one exists.
public sealed record NexusFileReplacement(
    int OldFileId,
    string? OldFileName,
    int NewFileId,
    string? NewFileName,
    DateTimeOffset? UploadedUtc);

public sealed record NexusModFileListing(
    IReadOnlyList<NexusModFile> Files,
    IReadOnlyList<NexusFileReplacement> Replacements)
{
    public static NexusModFileListing Empty { get; } = new([], []);
}

// Read-only, and deliberately so: there is no download and no endorsement here. Every failure comes
// back named, because "Nexus said nothing changed" and "BEM could not ask Nexus" are opposite
// statements and a caller that cannot tell them apart will print the wrong one.
public sealed class NexusClient(INexusTransport transport)
{
    public static string UnreachableMessage => Strings.Current["Core.Nexus.Client.Unreachable"];

    public async Task<NexusResult<NexusUser>> ValidateAsync(NexusApiKey apiKey, CancellationToken cancellationToken) =>
        await ReadAsync(NexusEndpoints.ValidateKey, apiKey, ReadUser, cancellationToken,
            unauthorizedMessage: Strings.Current["Core.Nexus.Client.Validate.Unauthorized"],
            okMessage: user => Strings.Current.Format("Core.Nexus.Client.Validate.Ok", user.Name));

    public async Task<NexusResult<IReadOnlyList<NexusUpdatedMod>>> GetUpdatedModsAsync(
        NexusApiKey apiKey,
        NexusUpdatePeriod period,
        CancellationToken cancellationToken) =>
        await ReadAsync(NexusEndpoints.UpdatedMods(period), apiKey, ReadUpdatedMods, cancellationToken,
            unauthorizedMessage: Strings.Current["Core.Nexus.Client.UpdatedMods.Unauthorized"],
            okMessage: mods => Strings.Current.Plural("Core.Nexus.Client.UpdatedMods.Ok", mods.Count, NexusEndpoints.Describe(period)));

    public async Task<NexusResult<IReadOnlyList<NexusDownloadUrl>>> GetDownloadUrlsAsync(
        NexusApiKey apiKey,
        NxmLink link,
        CancellationToken cancellationToken) =>
        await ReadAsync(
            NexusEndpoints.DownloadLink(link.ModId ?? 0, link.FileId ?? 0, link.DownloadKey, link.Expires),
            apiKey, ReadDownloadUrls, cancellationToken,
            unauthorizedMessage: Strings.Current["Core.Nexus.Client.DownloadUrls.Unauthorized"],
            okMessage: urls => Strings.Current.Plural("Core.Nexus.Client.DownloadUrls.Ok", urls.Count));

    public async Task<NexusResult<NexusModDetail>> GetModAsync(
        NexusApiKey apiKey,
        int modId,
        CancellationToken cancellationToken) =>
        await ReadAsync(NexusEndpoints.Mod(modId), apiKey, ReadModDetail, cancellationToken,
            unauthorizedMessage: Strings.Current["Core.Nexus.Client.Mod.Unauthorized"],
            okMessage: mod => Strings.Current.Format(
                "Core.Nexus.Client.Mod.Ok", mod.Name, mod.Version ?? Strings.Current["Core.Nexus.Client.UnstatedVersion"]));

    // Every file on the page rather than the page's headline version. This is the only lookup that can
    // say whether the exact file somebody installed is still one Nexus lists, which for an optional
    // file, a patch or a support file is a different question from what the mod page's version says.
    public async Task<NexusResult<NexusModFileListing>> GetModFilesAsync(
        NexusApiKey apiKey,
        int modId,
        CancellationToken cancellationToken) =>
        await ReadAsync(NexusEndpoints.ModFiles(modId), apiKey, ReadModFiles, cancellationToken,
            unauthorizedMessage: Strings.Current["Core.Nexus.Client.ModFiles.Unauthorized"],
            okMessage: listing => Strings.Current.Plural("Core.Nexus.Client.ModFiles.Ok", listing.Files.Count));

    // Asks Nexus what a set of bytes is, by their hash. The archive is usually long gone by the time
    // this runs, which is exactly why the hash is taken while it is still there.
    public async Task<NexusResult<NexusFileIdentity>> FindFileByMd5Async(
        NexusApiKey apiKey,
        string md5,
        CancellationToken cancellationToken) =>
        await ReadAsync(NexusEndpoints.FileByMd5(md5), apiKey, ReadFileIdentity, cancellationToken,
            unauthorizedMessage: Strings.Current["Core.Nexus.Client.FindFile.Unauthorized"],
            okMessage: file => file.Version is { Length: > 0 } version
                ? Strings.Current.Format("Core.Nexus.Client.FindFile.OkWithVersion", file.ModId, version)
                : Strings.Current.Format("Core.Nexus.Client.FindFile.Ok", file.ModId),
            notFoundMessage: Strings.Current["Core.Nexus.Client.FindFile.NotFound"]);

    private async Task<NexusResult<T>> ReadAsync<T>(
        string path,
        NexusApiKey apiKey,
        Func<string, T?> parse,
        CancellationToken cancellationToken,
        string unauthorizedMessage,
        Func<T, string> okMessage,
        string? notFoundMessage = null)
    {
        NexusHttpResponse response;

        try
        {
            response = await transport.GetAsync(path, apiKey, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new NexusResult<T>(NexusOutcome.Canceled, default, Strings.Current["Core.Nexus.Client.Canceled"], NexusRateLimit.Unknown);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The message is the transport's own, never the request, so nothing here can carry a key.
            return new NexusResult<T>(NexusOutcome.Unreachable, default, $"{UnreachableMessage} {ex.Message}", NexusRateLimit.Unknown);
        }

        var rateLimit = NexusRateLimit.FromHeaders(response.Headers);

        if (response.TransportError is { } error)
            return new NexusResult<T>(NexusOutcome.Unreachable, default, $"{UnreachableMessage} {error}", rateLimit);

        if (response.StatusCode is 401 or 403)
            return new NexusResult<T>(NexusOutcome.Unauthorized, default, unauthorizedMessage, rateLimit);

        if (response.StatusCode == 404 && notFoundMessage is not null)
            return new NexusResult<T>(NexusOutcome.NotFound, default, notFoundMessage, rateLimit);

        if (response.StatusCode == 429)
            return new NexusResult<T>(NexusOutcome.RateLimited, default,
                Strings.Current.Format("Core.Nexus.Client.RateLimited", rateLimit.Describe()), rateLimit);

        if (!response.IsSuccess)
            return new NexusResult<T>(NexusOutcome.ServiceError, default,
                Strings.Current.Format("Core.Nexus.Client.ServiceError", response.StatusCode), rateLimit);

        T? value;

        try
        {
            value = parse(response.Body);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            value = default;
        }

        return value is null
            ? new NexusResult<T>(NexusOutcome.Malformed, default,
                Strings.Current["Core.Nexus.Client.Malformed"], rateLimit)
            : new NexusResult<T>(NexusOutcome.Ok, value, okMessage(value), rateLimit);
    }

    private static NexusUser? ReadUser(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;

        return root.ValueKind != JsonValueKind.Object
            ? null
            : new NexusUser(
                Int(root, "user_id") ?? 0,
                Text(root, "name") ?? Strings.Current["Core.Nexus.Client.UnnamedAccount"],
                Bool(root, "is_premium"),
                Bool(root, "is_supporter"));
    }

    private static IReadOnlyList<NexusUpdatedMod>? ReadUpdatedMods(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;

        if (root.ValueKind != JsonValueKind.Array)
            return null;

        var mods = new List<NexusUpdatedMod>();

        foreach (var element in root.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object || Int(element, "mod_id") is not { } modId)
                continue;

            mods.Add(new NexusUpdatedMod(
                modId,
                DateTimeOffset.FromUnixTimeSeconds(Long(element, "latest_file_update") ?? 0),
                DateTimeOffset.FromUnixTimeSeconds(Long(element, "latest_mod_activity") ?? 0)));
        }

        return mods;
    }

    private static IReadOnlyList<NexusDownloadUrl>? ReadDownloadUrls(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;

        if (root.ValueKind != JsonValueKind.Array)
            return null;

        var urls = new List<NexusDownloadUrl>();

        foreach (var element in root.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object && Text(element, "URI") is { } uri)
                urls.Add(new NexusDownloadUrl(Text(element, "name") ?? "an unnamed location", uri));
        }

        return urls.Count == 0 ? null : urls;
    }

    private static NexusModDetail? ReadModDetail(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;

        return root.ValueKind != JsonValueKind.Object || Int(root, "mod_id") is not { } modId
            ? null
            : new NexusModDetail(
                modId,
                Text(root, "name") ?? Strings.Current["Core.Nexus.Client.UnnamedMod"],
                Text(root, "version"),
                Long(root, "updated_timestamp") is { } stamp ? DateTimeOffset.FromUnixTimeSeconds(stamp) : null,
                Bool(root, "available"),
                Bool(root, "contains_adult_content"));
    }

    // An answer with no files array at all is an answer BEM could not read, and is reported as such:
    // "this mod has no files" is a claim Nexus does not make and BEM must not invent. An empty
    // file_updates array is different and ordinary, because a page whose files have never been
    // replaced has nothing to record there.
    private static NexusModFileListing? ReadModFiles(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("files", out var files)
            || files.ValueKind != JsonValueKind.Array)
            return null;

        var listed = new List<NexusModFile>();

        foreach (var element in files.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object || Int(element, "file_id") is not { } fileId)
                continue;

            listed.Add(new NexusModFile(
                fileId,
                Text(element, "name"),
                Text(element, "file_name"),
                Text(element, "version"),
                Int(element, "category_id"),
                Text(element, "category_name"),
                Long(element, "uploaded_timestamp") is { } stamp ? DateTimeOffset.FromUnixTimeSeconds(stamp) : null));
        }

        var replacements = new List<NexusFileReplacement>();

        if (root.TryGetProperty("file_updates", out var updates) && updates.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in updates.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || Int(element, "old_file_id") is not { } oldFileId
                    || Int(element, "new_file_id") is not { } newFileId)
                    continue;

                replacements.Add(new NexusFileReplacement(
                    oldFileId,
                    Text(element, "old_file_name"),
                    newFileId,
                    Text(element, "new_file_name"),
                    Long(element, "uploaded_timestamp") is { } stamp ? DateTimeOffset.FromUnixTimeSeconds(stamp) : null));
            }
        }

        return new NexusModFileListing(listed, replacements);
    }

    // Nexus answers this one with a list of {mod, file_details} pairs, because one set of bytes can be
    // published on more than one page. More than one answer is no answer: naming the wrong mod page is
    // how a user is told to update a mod they do not have.
    private static NexusFileIdentity? ReadFileIdentity(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;

        var matches = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().ToList(),
            JsonValueKind.Object => [root],
            _ => new List<JsonElement>()
        };

        if (matches.Count != 1 || matches[0].ValueKind != JsonValueKind.Object)
            return null;

        var match = matches[0];

        var modId = match.TryGetProperty("mod", out var mod) && mod.ValueKind == JsonValueKind.Object
            ? Int(mod, "mod_id")
            : Int(match, "mod_id");

        if (modId is not { } id)
            return null;

        var file = match.TryGetProperty("file_details", out var details) && details.ValueKind == JsonValueKind.Object
            ? details
            : match;

        return new NexusFileIdentity(
            id,
            Int(file, "file_id"),
            Text(file, "file_name") ?? Text(file, "name"),
            Text(file, "version"),
            Long(file, "uploaded_timestamp") is { } stamp ? DateTimeOffset.FromUnixTimeSeconds(stamp) : null);
    }

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static long? Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
