using System.Globalization;
using System.Text.Json;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Butr;

public sealed record ButrHttpResponse(int StatusCode, string Body, string? TransportError = null)
{
    public static ButrHttpResponse Failed(string error) => new(0, string.Empty, error);

    public bool IsSuccess => TransportError is null && StatusCode is >= 200 and < 300;
}

// Keeps Core free of HttpClient. The BUTR endpoint is anonymous: no key, no token, no account.
public interface IButrTransport
{
    Task<ButrHttpResponse> PostJsonAsync(string path, string body, CancellationToken cancellationToken);
}

public sealed record ButrModuleRequest(string ModuleId, string? ModuleVersion, string DisplayName);

public sealed record ButrModuleScore(
    string ModuleId,
    string DisplayName,
    double Compatibility,
    double? RecommendedCompatibility,
    string? RecommendedVersion);

public enum ButrOutcome
{
    Ok,
    Unreachable,
    ServiceError,
    Malformed,
    Canceled
}

public sealed record ButrResult(ButrOutcome Outcome, IReadOnlyList<ButrModuleScore>? Scores, string Message);

// BUTR's compatibility scores are computed from crash reports the community has uploaded through
// ButterLib. The endpoint is anonymous and takes no credential of any kind: what leaves the machine
// is the list of module ids and versions plus the game version, and nothing else.
//
// It is an internal endpoint with no published contract. It has already been renamed once with no
// deprecation and no alias, which left BUTR's own Vortex client calling a dead path for years, so
// every failure here is named and none of them is read as good news.
public sealed class ButrClient(IButrTransport transport)
{
    public static string UnreachableMessage => Strings.Current["Core.Butr.Client.Unreachable"];

    public async Task<ButrResult> GetScoresAsync(
        string gameVersion,
        IReadOnlyList<ButrModuleRequest> modules,
        CancellationToken cancellationToken)
    {
        ButrHttpResponse response;

        try
        {
            response = await transport.PostJsonAsync(ButrEndpoints.CompatibilityScores, BuildBody(gameVersion, modules), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new ButrResult(ButrOutcome.Canceled, null, Strings.Current["Core.Butr.Client.Canceled"]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ButrResult(ButrOutcome.Unreachable, null, $"{UnreachableMessage} {ex.Message}");
        }

        if (response.TransportError is { } error)
            return new ButrResult(ButrOutcome.Unreachable, null, $"{UnreachableMessage} {error}");

        if (!response.IsSuccess)
            return new ButrResult(ButrOutcome.ServiceError, null,
                response.StatusCode == 404
                    ? Strings.Current["Core.Butr.Client.Moved"]
                    : Strings.Current.Format("Core.Butr.Client.ServiceError", response.StatusCode));

        try
        {
            return Read(response.Body, modules) is { } scores
                ? new ButrResult(ButrOutcome.Ok, scores, Strings.Current.Plural("Core.Butr.Client.ScoresReturned", scores.Count))
                : new ButrResult(ButrOutcome.Malformed, null, Strings.Current["Core.Butr.Client.Malformed"]);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return new ButrResult(ButrOutcome.Malformed, null, Strings.Current["Core.Butr.Client.Malformed"]);
        }
    }

    // Camel case, matching what both of BUTR's own clients send.
    public static string BuildBody(string gameVersion, IReadOnlyList<ButrModuleRequest> modules)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("gameVersion", gameVersion);
            writer.WriteStartArray("modules");

            foreach (var module in modules)
            {
                writer.WriteStartObject();
                writer.WriteString("moduleId", module.ModuleId);
                writer.WriteString("moduleVersion", module.ModuleVersion ?? string.Empty);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static IReadOnlyList<ButrModuleScore>? Read(string body, IReadOnlyList<ButrModuleRequest> modules)
    {
        var root = JsonDocument.Parse(body).RootElement;

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("modules", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;

        var names = modules.ToDictionary(module => module.ModuleId, module => module.DisplayName, StringComparer.OrdinalIgnoreCase);
        var scores = new List<ButrModuleScore>();

        foreach (var element in list.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || element.TryGetProperty("moduleId", out var id) is false
                || id.GetString() is not { Length: > 0 } moduleId
                || Number(element, "compatibility") is not { } compatibility)
                continue;

            scores.Add(new ButrModuleScore(
                moduleId,
                names.GetValueOrDefault(moduleId, moduleId),
                compatibility,
                Number(element, "recommendedCompatibility"),
                element.TryGetProperty("recommendedModuleVersion", out var version) && version.ValueKind == JsonValueKind.String
                    ? version.GetString()
                    : null));
        }

        return scores;
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;
}

public static class ButrEndpoints
{
    public const string BaseUrl = "https://sitenexusmods.butr.link/";

    public const string CompatibilityScores = "api/v1/mods-analyzer/compatibility-scores";

    // Exchanges a Nexus API key for a BUTR token. The key goes to BUTR and never to Nexus from here:
    // BUTR validates it against Nexus itself, which is how its own Vortex client signs in.
    public const string Authenticate = "api/v1/authentication/authenticate";

    // Which Nexus mod publishes which module id, read by BUTR out of the SubModule.xml inside the
    // mod's own download. Readable to any signed-in Nexus account, no role required.
    public const string ExposedNexusModsMods = "api/v1/exposed-mods/nexus-mods-mod/paginated";

    public const string ApiKeyHeaderName = "apiKey";

    public const string AuthorizationHeaderName = "Authorization";

    public const string AuthorizationScheme = "BUTR-NexusMods";

    // The server binds this from a header and refuses the request without it. 1 is Bannerlord.
    public const string TenantHeaderName = "Tenant";

    public const string BannerlordTenant = "1";

    public const string ProjectUrl = "https://github.com/BUTR/BUTR.Site.NexusMods";

    // Where ButterLib's own crash uploader posts, read out of its BUTRUploadUrl assembly metadata.
    // The service routes it to CrashUploadController's /services/crash-upload.py, which is anonymous
    // and rate limited, and it is the only way anything reaches the dataset the scores are counted
    // from. There is no endpoint for a run that went well.
    public const string CrashUploadUrl = "https://crash.butr.link/upload";

    // The server switches on this to pick its reader. 14 is the current model.
    public const string ReportVersionHeaderName = "CrashReportVersion";

    public const string CrashProjectUrl = "https://github.com/BUTR/BUTR.CrashReportServer";
}

// BUTR stores the game version as an exact string of three components. Sending the change set as
// well matches no stored row at all, which is why BUTR's own Vortex client has returned nothing for
// years.
public static class ButrGameVersion
{
    public static string? Format(string? raw)
    {
        var text = raw?.Trim();

        if (string.IsNullOrEmpty(text))
            return null;

        var prefix = char.IsLetter(text[0]) ? text[0].ToString() : string.Empty;
        var digits = text[prefix.Length..].Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (digits.Length == 0 || !digits.Take(Math.Min(3, digits.Length))
                .All(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            return null;

        var components = digits.Take(3).Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToList();

        while (components.Count < 3)
            components.Add(0);

        return $"{prefix}{string.Join('.', components)}";
    }
}
