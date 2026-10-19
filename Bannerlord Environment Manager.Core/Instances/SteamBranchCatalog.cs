using System.Text.Json;

namespace BannerlordEnvironmentManager.Core.Instances;

public sealed record SteamBranch(string Name, string Description, string BuildId, bool NeedsPassword);

// steamcmd.net mirrors Steam's own branch list for a public appid with no login needed, which is the
// only way to read it without asking BEM to hold Steam credentials at all.
public static class SteamBranchCatalog
{
    private static readonly string AppId = GameDlc.BaseAppId.ToString();

    public static IReadOnlyList<SteamBranch> Parse(string json) => Parse(json, AppId);

    // A DLC is its own Steam app with its own branch list, which does not mirror the base app's: it
    // has its own coverage, its own build ids, and sometimes its own branch names. appId lets this
    // read any app's branches out of the same document shape, not only Bannerlord's own.
    public static IReadOnlyList<SteamBranch> Parse(string json, string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        if (string.IsNullOrWhiteSpace(json))
            return [];

        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty(appId, out var app)
            || !app.TryGetProperty("depots", out var depots)
            || !depots.TryGetProperty("branches", out var branches)
            || branches.ValueKind != JsonValueKind.Object)
            return [];

        var result = new List<SteamBranch>();

        foreach (var branch in branches.EnumerateObject())
        {
            var value = branch.Value;

            result.Add(new SteamBranch(
                branch.Name,
                StringOrEmpty(value, "description"),
                StringOrEmpty(value, "buildid"),
                StringOrEmpty(value, "pwdrequired") == "1"));
        }

        return result
            .OrderBy(branch => Priority(branch.Name))
            .ThenBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<IReadOnlyList<SteamBranch>> FetchAsync(CancellationToken cancellationToken) =>
        Parse(await FetchJsonAsync(cancellationToken));

    public static async Task<IReadOnlyList<SteamBranch>> FetchAsync(string appId, CancellationToken cancellationToken) =>
        Parse(await FetchJsonAsync(appId, cancellationToken), appId);

    // The raw response, because the same document carries the depots and manifest ids the Steam
    // client's own console needs, and fetching it twice for two readings of one document is waste.
    // An empty string is the offline answer: Parse returns nothing for it, as it does for a body that
    // is not the app info.
    public static Task<string> FetchJsonAsync(CancellationToken cancellationToken) =>
        FetchJsonAsync(AppId, cancellationToken);

    public static async Task<string> FetchJsonAsync(string appId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        try
        {
            using var client = new HttpClient();
            return await client.GetStringAsync($"https://api.steamcmd.net/v1/info/{appId}", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _ = ex;
            return string.Empty;
        }
    }

    private static string StringOrEmpty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int Priority(string name) => name switch
    {
        SteamAppManifest.PublicBranch => 0,
        "beta" => 1,
        _ => 2
    };
}
