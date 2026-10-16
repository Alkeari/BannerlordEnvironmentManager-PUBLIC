namespace BannerlordEnvironmentManager.Core.Instances;

// What is knowable about a DLC app's own branch before a download of it is attempted, read from
// Steam's public app-info mirror. A DLC is its own Steam app with its own branch list, which does
// not mirror the base app's - a branch the base game publishes is not proof the DLC publishes the
// same name, and this investigation found at least one base branch (v1.4.8) with no matching entry
// under War Sails' own app at all. CatalogAvailable false means the branch list itself could not be
// read (an empty or unparseable response, most likely a network problem); it never means "assume
// the branch is fine" - a caller that cannot tell whether a branch is published must refuse rather
// than guess.
public sealed record DlcCatalogInfo(bool CatalogAvailable, bool BranchPublished, long PublishedSizeBytes)
{
    public static DlcCatalogInfo Unknown { get; } = new(false, false, 0);
}

public static class DlcCatalog
{
    // Pure: reads one already-fetched app-info document. The same document that answers a DLC's
    // branch list also carries its depots, so one fetch answers both questions this needs - whether
    // the branch exists at all, and, if it does, how many bytes a complete download of it weighs
    // (the figure DlcDownloadStep compares actual bytes on disk against, the same way the base
    // download already does for the base app).
    public static DlcCatalogInfo Read(string json, int dlcAppId, string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        if (string.IsNullOrWhiteSpace(json))
            return DlcCatalogInfo.Unknown;

        var appId = dlcAppId.ToString();
        var branches = SteamBranchCatalog.Parse(json, appId);

        // No branches parsed reads the same as no response at all: a real DLC app's own info always
        // carries at least one branch, so an empty list here means the document did not answer this
        // question, not that the DLC genuinely publishes nothing.
        if (branches.Count == 0)
            return DlcCatalogInfo.Unknown;

        if (!branches.Any(candidate => string.Equals(candidate.Name, branch, StringComparison.Ordinal)))
            return new DlcCatalogInfo(true, false, 0);

        var depots = SteamDepotCatalog.Parse(json, appId, branch);

        return new DlcCatalogInfo(true, true, SteamDepotCatalog.InstalledSizeBytes(depots));
    }

    public static async Task<DlcCatalogInfo> FetchAsync(int dlcAppId, string branch, CancellationToken cancellationToken) =>
        Read(await SteamBranchCatalog.FetchJsonAsync(dlcAppId.ToString(), cancellationToken), dlcAppId, branch);
}
