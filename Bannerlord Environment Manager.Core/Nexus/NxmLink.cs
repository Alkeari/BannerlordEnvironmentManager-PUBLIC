using System.Globalization;

namespace BannerlordEnvironmentManager.Core.Nexus;

public enum NxmLinkKind
{
    Unrecognized,
    BannerlordMod,
    OtherGameMod,
    Collection,
    OAuthCallback,
    Premium
}

// The nxm scheme carries four different messages, not just downloads. Vortex completes its browser
// login through nxm://oauth/callback, so a handler that treats everything as a download silently
// stops the user signing in to Vortex, and they will never guess why. Every shape is classified here
// and only one of them is BEM's.
public sealed record NxmLink(
    NxmLinkKind Kind,
    string Raw,
    string? GameDomain = null,
    int? ModId = null,
    int? FileId = null,
    string? DownloadKey = null,
    long? Expires = null,
    long? UserId = null)
{
    public bool IsBannerlordDownload => Kind == NxmLinkKind.BannerlordMod;

    // The download key is a short-lived grant bound to one file and one Nexus account. It is a
    // credential, so this is the only form of the link that anything may write down.
    public string Redacted =>
        DownloadKey is null ? Raw : Raw.Replace(DownloadKey, "hidden", StringComparison.Ordinal);

    public bool HasExpired(DateTimeOffset now) =>
        Expires is { } expires && now > DateTimeOffset.FromUnixTimeSeconds(expires);

    public static NxmLink Parse(string? raw)
    {
        var text = raw?.Trim();

        if (string.IsNullOrEmpty(text)
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("nxm", StringComparison.OrdinalIgnoreCase))
            return new NxmLink(NxmLinkKind.Unrecognized, text ?? string.Empty);

        var host = uri.Host;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = ReadQuery(uri.Query);

        if (host.Equals("oauth", StringComparison.OrdinalIgnoreCase))
            return new NxmLink(NxmLinkKind.OAuthCallback, text);

        if (host.Equals("premium", StringComparison.OrdinalIgnoreCase))
            return new NxmLink(NxmLinkKind.Premium, text);

        if (segments.Length >= 2 && segments[0].Equals("collections", StringComparison.OrdinalIgnoreCase))
            return new NxmLink(NxmLinkKind.Collection, text, host);

        if (segments.Length < 4
            || !segments[0].Equals("mods", StringComparison.OrdinalIgnoreCase)
            || !segments[2].Equals("files", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(segments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var modId)
            || !int.TryParse(segments[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileId))
            return new NxmLink(NxmLinkKind.Unrecognized, text, host);

        var kind = host.Equals(NexusEndpoints.GameDomain, StringComparison.OrdinalIgnoreCase)
            ? NxmLinkKind.BannerlordMod
            : NxmLinkKind.OtherGameMod;

        return new NxmLink(
            kind,
            text,
            host,
            modId,
            fileId,
            query.GetValueOrDefault("key"),
            long.TryParse(query.GetValueOrDefault("expires"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var expires) ? expires : null,
            long.TryParse(query.GetValueOrDefault("user_id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId) ? userId : null);
    }

    private static Dictionary<string, string> ReadQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');

            if (separator > 0)
                values[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return values;
    }
}
