using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

public enum NexusUpdatePeriod
{
    Day,
    Week,
    Month
}

// Every path here is relative to BaseUrl and carries no credential: the key travels in the apikey
// header and nowhere else, because a key in a query string ends up in every proxy log on the way.
public static class NexusEndpoints
{
    public const string BaseUrl = "https://api.nexusmods.com/v1/";

    public const string GameDomain = NexusArchiveName.GameDomain;

    // The Acceptable Use Policy requires an application name and version on every request, and names
    // sending blank or impersonating metadata as unacceptable use.
    public const string ApplicationName = "Bannerlord Environment Manager";

    public const string AcceptableUsePolicyUrl = "https://help.nexusmods.com/article/114-api-acceptable-use-policy";

    // Documented as exempt from the hourly limit, which is why it is the validation call.
    public static string ValidateKey => "users/validate.json";

    public static string UpdatedMods(NexusUpdatePeriod period) =>
        $"games/{GameDomain}/mods/updated.json?period={QueryValue(period)}";

    public static string Game => $"games/{GameDomain}.json";

    public static string Mod(int modId) =>
        $"games/{GameDomain}/mods/{modId}.json";

    // Every file on a mod page, each carrying its own version and its own category. A mod page's own
    // version field tracks its main file, so comparing an optional file, a patch or an MCM support file
    // against it compares two unrelated numbers: this is the only place Nexus says whether the exact
    // file somebody installed is still one of the current ones.
    // POST, with the mod's version in the body. Nexus refuses it for a mod the account has not
    // downloaded, so a caller offers it only for a mod BEM knows the page of and only when signed in.
    public static string Endorse(int modId) =>
        $"games/{GameDomain}/mods/{modId}/endorse.json";

    // The account's tracked mods across every game; callers keep this game's. Tracking is a POST to the
    // same path with the game in the query and the mod id in the body.
    public static string TrackedMods => "user/tracked_mods.json";

    public static string TrackMod => $"user/tracked_mods.json?domain_name={GameDomain}";

    public static string ModFiles(int modId) =>
        $"games/{GameDomain}/mods/{modId}/files.json";

    // The only lookup Nexus publishes that is keyed on what a file contains rather than on what it is
    // called. It is what turns an archive somebody downloaded by hand, under any name, into the exact
    // mod page, file id and file version - which is the difference between guessing from a filename
    // and knowing. Documented as available to every account, premium or not.
    public static string FileByMd5(string md5) =>
        $"games/{GameDomain}/mods/md5_search/{Uri.EscapeDataString(md5)}.json";

    // The only endpoint that carries a key in its query, and the key is not BEM's API key: it is the
    // single-file, time-limited download grant the Nexus website mints when the user clicks Mod
    // Manager Download. Nexus requires it here for anyone who is not a premium member, and states
    // that requirement in its own specification.
    public static string DownloadLink(int modId, int fileId, string? grantKey, long? expires) =>
        grantKey is null || expires is null
            ? $"games/{GameDomain}/mods/{modId}/files/{fileId}/download_link.json"
            : $"games/{GameDomain}/mods/{modId}/files/{fileId}/download_link.json"
              + $"?key={Uri.EscapeDataString(grantKey)}&expires={expires}";

    // A collection revision's bundle link, as the v2 API names it ("/v2/collections/N/revisions/M/download_link"),
    // reached from the v1 base by stepping up one level.
    public static string CollectionBundle(string revisionDownloadLink) => "../" + revisionDownloadLink.TrimStart('/');

    // Nexus caches the feed per period and accepts exactly these three values.
    public static string QueryValue(NexusUpdatePeriod period) => period switch
    {
        NexusUpdatePeriod.Day => "1d",
        NexusUpdatePeriod.Month => "1m",
        _ => "1w"
    };

    public static string Describe(NexusUpdatePeriod period) => period switch
    {
        NexusUpdatePeriod.Day => Strings.Current["Core.Nexus.Endpoints.Period.Day"],
        NexusUpdatePeriod.Month => Strings.Current["Core.Nexus.Endpoints.Period.Month"],
        _ => Strings.Current["Core.Nexus.Endpoints.Period.Week"]
    };
}
