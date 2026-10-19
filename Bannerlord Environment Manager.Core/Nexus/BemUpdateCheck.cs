using System.Text.Json;

namespace BannerlordEnvironmentManager.Core.Nexus;

public enum BemUpdateVerdict
{
    UpToDate,
    NewerAvailable,
    CannotTell
}

// What Nexus said BEM's page is at. Version is null whenever no answer arrived, and then exactly one of
// StatusCode (an answer that was not a version) or TransportError (no answer at all) says why.
public sealed record BemPublishedVersion(string? Version, int StatusCode = 0, string? TransportError = null)
{
    public bool Found => !string.IsNullOrWhiteSpace(Version);
}

// BEM checking on itself: its own Nexus page. Kept apart from the module update machinery because BEM
// is not a module - it has no folder in the game, no SubModule.xml, and nothing to install through the
// pipeline. All this can honestly offer is "a newer BEM exists, here is its page", and that is all it
// offers.
public static class BemUpdateCheck
{
    public const int ModId = 11049;

    // Nexus's numeric id for Mount & Blade II: Bannerlord, which the v2 API keys a mod on instead of the
    // game domain the v1 API uses.
    public const int NexusGameId = 3174;

    public const string PageUrl = "https://www.nexusmods.com/mountandblade2bannerlord/mods/11049";

    public static string VersionQuery => $"{{ mod(modId: {ModId}, gameId: {NexusGameId}) {{ version }} }}";

    // Asked of the public API, so it works for anyone, signed in to Nexus or not.
    public static async Task<BemPublishedVersion> FetchPublishedAsync(
        INexusPublicQuery nexus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nexus);

        var response = await nexus.QueryAsync(VersionQuery, cancellationToken);

        if (response.TransportError is { } error)
            return new BemPublishedVersion(null, TransportError: error);

        if (!response.IsSuccess)
            return new BemPublishedVersion(null, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(response.Body);

            return document.RootElement.TryGetProperty("data", out var data)
                   && data.ValueKind == JsonValueKind.Object
                   && data.TryGetProperty("mod", out var mod)
                   && mod.ValueKind == JsonValueKind.Object
                   && mod.TryGetProperty("version", out var version)
                   && version.ValueKind == JsonValueKind.String
                ? new BemPublishedVersion(version.GetString(), response.StatusCode)
                : new BemPublishedVersion(null, response.StatusCode);
        }
        catch (JsonException)
        {
            return new BemPublishedVersion(null, response.StatusCode);
        }
    }

    // CannotTell rather than a guess when either side does not parse: Nexus's version field is
    // whatever the author typed, and a string comparison that cannot be defended is worse than
    // saying nothing. A published version that parses lower than the installed one reads as
    // UpToDate, which is what running an unreleased build should look like.
    public static BemUpdateVerdict Compare(string? installedVersion, string? publishedVersion)
    {
        if (!TryParse(installedVersion, out var installed) || !TryParse(publishedVersion, out var published))
            return BemUpdateVerdict.CannotTell;

        return published > installed ? BemUpdateVerdict.NewerAvailable : BemUpdateVerdict.UpToDate;
    }

    // Nexus authors write versions with and without a leading v, and with two or three parts.
    private static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim().TrimStart('v', 'V');

        if (!trimmed.Contains('.'))
            trimmed += ".0";

        return Version.TryParse(trimmed, out var parsed) && (version = parsed) is not null;
    }
}
