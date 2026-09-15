using System.Globalization;
using System.Text.Json;

namespace BannerlordEnvironmentManager.Core.Instances;

// One depot of one branch, and the manifest that pins it to that branch's build. DownloadBytes is
// what crosses the wire, SizeBytes is what lands on disk.
public sealed record SteamDepot(string DepotId, string ManifestId, long DownloadBytes, long SizeBytes)
{
    // The Steam client's own console command. The client is already signed in, so this is the one
    // route to a specific build that needs no second sign-in and no password: the QR flow
    // DepotDownloader drives needs a phone in front of the screen, which a remote desktop session
    // does not have.
    public string ConsoleCommand => $"download_depot {DepotDownloaderTool.BannerlordAppId} {DepotId} {ManifestId}";
}

public static class SteamDepotCatalog
{
    private static readonly string AppId = GameDlc.BaseAppId.ToString();

    // What Steam downloads for a branch, from the same app info the branch list comes from. Depots
    // marked shared or belonging to another app are the DirectX and Visual C++ redistributables that
    // every Steam game installs machine-wide; they are not part of a version and are skipped.
    public static IReadOnlyList<SteamDepot> Parse(string json, string branch) => Parse(json, AppId, branch);

    // A DLC publishes its own depots under its own app id, in the same document shape as the base
    // app's. appId lets this read any app's depots, not only Bannerlord's own.
    public static IReadOnlyList<SteamDepot> Parse(string json, string appId, string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        if (string.IsNullOrWhiteSpace(json))
            return [];

        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty(appId, out var app)
            || !app.TryGetProperty("depots", out var depots)
            || depots.ValueKind != JsonValueKind.Object)
            return [];

        var found = new List<SteamDepot>();

        foreach (var depot in depots.EnumerateObject())
        {
            if (!depot.Name.All(char.IsAsciiDigit))
                continue;

            var value = depot.Value;

            if (value.TryGetProperty("sharedinstall", out _) || value.TryGetProperty("depotfromapp", out _))
                continue;

            if (!value.TryGetProperty("manifests", out var manifests)
                || !manifests.TryGetProperty(branch, out var manifest)
                || Text(manifest, "gid") is not { Length: > 0 } gid)
                continue;

            found.Add(new SteamDepot(depot.Name, gid, Bytes(manifest, "download"), Bytes(manifest, "size")));
        }

        return [.. found.OrderBy(depot => depot.DepotId, StringComparer.Ordinal)];
    }

    public static long TotalDownloadBytes(IReadOnlyList<SteamDepot> depots)
    {
        ArgumentNullException.ThrowIfNull(depots);

        return depots.Sum(depot => depot.DownloadBytes);
    }

    // The depot the sign-in run asks for a manifest of. Manifests are sized by how many files and
    // chunks a depot holds, so the smallest depot is the cheapest thing on the branch that still
    // proves the account can reach it. A branch whose depot list could not be fetched has none, and
    // the sign-in run then asks for no depot in particular.
    public static SteamDepot? SmallestDepot(IReadOnlyList<SteamDepot> depots)
    {
        ArgumentNullException.ThrowIfNull(depots);

        return depots.Count == 0 ? null : depots.MinBy(depot => depot.DownloadBytes);
    }

    public static long TotalSizeBytes(IReadOnlyList<SteamDepot> depots)
    {
        ArgumentNullException.ThrowIfNull(depots);

        return depots.Sum(depot => depot.SizeBytes);
    }

    // What a finished install weighs, which is not the sum: Bannerlord's two content depots both
    // carry Modules\SandBox and Modules\StoryMode, so those files are published twice and written
    // once. The union cannot be smaller than the largest depot, and that floor is the only figure
    // the published data supports. Summing instead put the bar 4.9 GB above anything that can ever
    // land on disk, and a complete v1.5.2 was rejected as unfinished every time it was checked.
    public static long InstalledSizeBytes(IReadOnlyList<SteamDepot> depots)
    {
        ArgumentNullException.ThrowIfNull(depots);

        return depots.Count == 0 ? 0 : depots.Max(depot => depot.SizeBytes);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long Bytes(JsonElement element, string name) =>
        long.TryParse(Text(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) ? bytes : 0;
}
