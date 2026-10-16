using System.Text.Json;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Butr;

public readonly record struct ButrHeader(string Name, string Value);

public sealed record ButrAuthenticatedRequest(string Path, string Body, IReadOnlyList<ButrHeader> Headers);

// Kept apart from IButrTransport because that one is anonymous by contract and this one is not. A
// caller holding only the anonymous transport cannot accidentally send a credential.
public interface IButrAuthenticatedTransport
{
    Task<ButrHttpResponse> PostJsonAsync(ButrAuthenticatedRequest request, CancellationToken cancellationToken);
}

public enum ButrIndexOutcome
{
    Ok,
    NoApiKey,
    Rejected,
    Unreachable,
    ServiceError,
    Malformed,
    Canceled
}

public sealed record ButrExposedMod(int NexusModId, IReadOnlyList<string> ModuleIds);

public sealed record ButrExposedPage(IReadOnlyList<ButrExposedMod> Mods, int CurrentPage, int TotalPages, int TotalCount);

// BUTR downloads every Bannerlord mod on Nexus and reads the Id out of whatever SubModule.xml is
// inside it, so this index says which mod page publishes which module id. That is the one source
// that does not need the archive a module was installed from to still exist.
//
// The whole index is read rather than one question asked per module, because 155 questions is 155
// round trips and the answer to "which mod pages publish this module id" is only trustworthy when
// the count of them is known.
public sealed class ButrModuleIndexClient(IButrAuthenticatedTransport transport)
{
    public static string UnreachableMessage => Strings.Current["Core.Butr.ModuleIndex.Unreachable"];

    public async Task<ButrTokenResult> AuthenticateAsync(string apiKey, CancellationToken cancellationToken)
    {
        var response = await Send(
            new ButrAuthenticatedRequest(
                ButrEndpoints.Authenticate,
                string.Empty,
                [new ButrHeader(ButrEndpoints.ApiKeyHeaderName, apiKey)]),
            cancellationToken);

        if (response.Failure is { } failure)
            return new ButrTokenResult(failure.Outcome, null, failure.Message);

        try
        {
            var root = JsonDocument.Parse(response.Body!).RootElement;

            if (ReadEnvelopeError(root) is { } error)
                return new ButrTokenResult(ButrIndexOutcome.Rejected,
                    null,
                    Strings.Current.Format("Core.Butr.ModuleIndex.KeyRefused", error));

            return root.TryGetProperty("value", out var value)
                   && value.ValueKind == JsonValueKind.Object
                   && value.TryGetProperty("token", out var token)
                   && token.GetString() is { Length: > 0 } jwt
                ? new ButrTokenResult(ButrIndexOutcome.Ok, jwt, Strings.Current["Core.Butr.ModuleIndex.SignedIn"])
                : new ButrTokenResult(ButrIndexOutcome.Malformed, null, MalformedMessage);
        }
        catch (JsonException)
        {
            return new ButrTokenResult(ButrIndexOutcome.Malformed, null, MalformedMessage);
        }
    }

    public async Task<ButrExposedPageResult> ReadExposedPageAsync(
        string token,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var response = await Send(
            new ButrAuthenticatedRequest(
                ButrEndpoints.ExposedNexusModsMods,
                Query(page, pageSize),
                [new ButrHeader(ButrEndpoints.AuthorizationHeaderName, $"{ButrEndpoints.AuthorizationScheme} {token}")]),
            cancellationToken);

        if (response.Failure is { } failure)
            return new ButrExposedPageResult(failure.Outcome, null, failure.Message);

        try
        {
            var root = JsonDocument.Parse(response.Body!).RootElement;

            if (ReadEnvelopeError(root) is { } error)
                return new ButrExposedPageResult(ButrIndexOutcome.ServiceError,
                    null,
                    Strings.Current.Format("Core.Butr.ModuleIndex.RequestRefused", error));

            return ReadPage(root) is { } read
                ? new ButrExposedPageResult(ButrIndexOutcome.Ok, read, Strings.Current.Plural("Core.Butr.ModuleIndex.PageRead", read.Mods.Count))
                : new ButrExposedPageResult(ButrIndexOutcome.Malformed, null, MalformedMessage);
        }
        catch (JsonException)
        {
            return new ButrExposedPageResult(ButrIndexOutcome.Malformed, null, MalformedMessage);
        }
    }

    public static string Query(int page, int pageSize) =>
        $$"""{"page":{{page}},"pageSize":{{pageSize}},"filters":[],"sortings":[{"property":"NexusModsModId","type":"ascending"}]}""";

    private static string MalformedMessage => Strings.Current["Core.Butr.ModuleIndex.Malformed"];

    // The server answers 200 with an error envelope for a rejected key, so a status code is never
    // read as success on its own.
    private static string? ReadEnvelopeError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("error", out var error)
            || error.ValueKind != JsonValueKind.Object)
            return null;

        return error.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } text
            ? text
            : error.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } fallback
                ? fallback
                : Strings.Current["Core.Butr.ModuleIndex.NoReasonGiven"];
    }

    private static ButrExposedPage? ReadPage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return null;

        var mods = new List<ButrExposedMod>();

        foreach (var element in items.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("nexusModsModId", out var id)
                || id.ValueKind != JsonValueKind.Number
                || !id.TryGetInt32(out var modId)
                || modId <= 0)
                continue;

            var moduleIds = new List<string>();

            if (element.TryGetProperty("modules", out var modules) && modules.ValueKind == JsonValueKind.Array)
            {
                foreach (var module in modules.EnumerateArray())
                {
                    if (module.ValueKind == JsonValueKind.Object
                        && module.TryGetProperty("moduleId", out var moduleId)
                        && moduleId.GetString() is { Length: > 0 } text)
                        moduleIds.Add(text);
                }
            }

            if (moduleIds.Count > 0)
                mods.Add(new ButrExposedMod(modId, moduleIds));
        }

        var metadata = value.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
            ? meta
            : default;

        return new ButrExposedPage(mods, Int(metadata, "currentPage"), Int(metadata, "totalPages"), Int(metadata, "totalCount"));
    }

    private static int Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : 0;

    private async Task<TransportRead> Send(ButrAuthenticatedRequest request, CancellationToken cancellationToken)
    {
        ButrHttpResponse response;

        try
        {
            response = await transport.PostJsonAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new TransportRead(null, new TransportFailure(ButrIndexOutcome.Canceled, Strings.Current["Core.Butr.ModuleIndex.Canceled"]));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new TransportRead(null, new TransportFailure(ButrIndexOutcome.Unreachable, $"{UnreachableMessage} {ex.Message}"));
        }

        if (response.TransportError is { } error)
            return new TransportRead(null, new TransportFailure(ButrIndexOutcome.Unreachable, $"{UnreachableMessage} {error}"));

        // An empty body cannot be told apart from a body of "null" once parsed, and both mean the
        // same thing here, so only a non-success status short-circuits.
        if (!response.IsSuccess)
            return new TransportRead(null, new TransportFailure(
                ButrIndexOutcome.ServiceError,
                response.StatusCode == 401
                    ? Strings.Current["Core.Butr.ModuleIndex.SignInRejected"]
                    : Strings.Current.Format("Core.Butr.ModuleIndex.StatusError", response.StatusCode)));

        return new TransportRead(response.Body, null);
    }

    private sealed record TransportFailure(ButrIndexOutcome Outcome, string Message);

    private sealed record TransportRead(string? Body, TransportFailure? Failure);
}

public sealed record ButrTokenResult(ButrIndexOutcome Outcome, string? Token, string Message);

public sealed record ButrExposedPageResult(ButrIndexOutcome Outcome, ButrExposedPage? Page, string Message);

public sealed record ButrModuleIndexRequest(string? ApiKey, int PageSize = 500, int MaxPages = 400);

// Both directions of the same table, because both are asked for: which mod pages publish a module
// id, and which module ids a mod page publishes.
public sealed record ButrModuleIndexResult(
    ButrIndexOutcome Outcome,
    string Message,
    IReadOnlyDictionary<string, IReadOnlyList<int>> NexusModIdsByModuleId,
    IReadOnlyDictionary<int, IReadOnlyList<string>> ModuleIdsByNexusModId,
    int ModsRead)
{
    public static ButrModuleIndexResult Nothing(ButrIndexOutcome outcome, string message) =>
        new(outcome, message,
            new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<int, IReadOnlyList<string>>(),
            0);
}

public sealed class ButrModuleIndex(ButrModuleIndexClient client)
{
    public static string NoApiKeyMessage => Strings.Current["Core.Butr.ModuleIndex.NoApiKey"];

    public async Task<ButrModuleIndexResult> ReadAsync(ButrModuleIndexRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            return ButrModuleIndexResult.Nothing(ButrIndexOutcome.NoApiKey, NoApiKeyMessage);

        var token = await client.AuthenticateAsync(request.ApiKey, cancellationToken);

        if (token is not { Outcome: ButrIndexOutcome.Ok, Token: { } jwt })
            return ButrModuleIndexResult.Nothing(token.Outcome, token.Message);

        var byModuleId = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
        var byModId = new Dictionary<int, List<string>>();
        var mods = 0;

        for (var page = 1; page <= request.MaxPages; page++)
        {
            var read = await client.ReadExposedPageAsync(jwt, page, request.PageSize, cancellationToken);

            // A page that fails after others succeeded would leave a partial index, and a module
            // absent from a partial index looks exactly like a module BUTR has never heard of. That
            // is the one confusion that turns "found nothing" into "could not look", so nothing
            // read so far is kept.
            if (read is not { Outcome: ButrIndexOutcome.Ok, Page: { } current })
                return ButrModuleIndexResult.Nothing(read.Outcome, read.Message);

            foreach (var mod in current.Mods)
            {
                mods++;
                byModId[mod.NexusModId] = [.. mod.ModuleIds];

                foreach (var moduleId in mod.ModuleIds)
                {
                    if (!byModuleId.TryGetValue(moduleId, out var ids))
                        byModuleId[moduleId] = ids = [];

                    ids.Add(mod.NexusModId);
                }
            }

            if (current.Mods.Count == 0 || (current.TotalPages > 0 && page >= current.TotalPages))
                break;
        }

        return new ButrModuleIndexResult(
            ButrIndexOutcome.Ok,
            Strings.Current.Plural("Core.Butr.ModuleIndex.Read", mods, byModuleId.Count),
            byModuleId.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<int>)[.. entry.Value], StringComparer.OrdinalIgnoreCase),
            byModId.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<string>)[.. entry.Value]),
            mods);
    }
}
