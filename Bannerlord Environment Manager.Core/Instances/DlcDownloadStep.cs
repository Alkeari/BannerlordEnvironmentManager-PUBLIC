using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Instances;

// One DLC download, fully decided: is the requested branch one this DLC app actually publishes,
// did the tool itself report failure, and - the base download's own rule, applied here too - did
// the right number of bytes actually land, since a quiet zero-transfer exit is not success. Every
// dependency arrives as a delegate, so this can be tested without a network call, a process, or a
// disk: fetchCatalogJson stands in for Steam's app-info mirror, runDownload for DepotDownloader,
// bytesOnDisk for the folder it wrote into.
public static class DlcDownloadStep
{
    public static async Task<DlcDownloadResult> RunAsync(
        int dlcAppId,
        string branch,
        Func<CancellationToken, Task<string>> fetchCatalogJson,
        Func<CancellationToken, Task<DlcDownloadResult?>> runDownload,
        Func<long> bytesOnDisk,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentNullException.ThrowIfNull(fetchCatalogJson);
        ArgumentNullException.ThrowIfNull(runDownload);
        ArgumentNullException.ThrowIfNull(bytesOnDisk);

        var dlcName = GameDlc.ById(dlcAppId)?.DisplayName ?? Strings.Current.Format("Core.Instances.DlcDownload.UnknownName", dlcAppId);
        var json = await fetchCatalogJson(cancellationToken);
        var catalog = DlcCatalog.Read(json, dlcAppId, branch);

        // No silent fallback: a branch list this cannot read is a branch list this does not know,
        // and sending the download anyway would be a guess wearing a confirmation dialog.
        if (!catalog.CatalogAvailable)
        {
            return new DlcDownloadResult(false,
                Strings.Current.Format("Core.Instances.DlcDownload.CatalogUnavailable", dlcName, branch));
        }

        if (!catalog.BranchPublished)
        {
            return new DlcDownloadResult(false,
                Strings.Current.Format("Core.Instances.DlcDownload.BranchNotPublished", dlcName, branch));
        }

        // The tool's own read of the run - a real Steam refusal, in Steam's own words - takes
        // priority over the completeness check below: it is the more specific answer when both would
        // otherwise fire.
        if (await runDownload(cancellationToken) is { Succeeded: false } toolFailure)
            return toolFailure;

        // The bytes decide, not the exit code, for the DLC the same as for the base game: a run that
        // exits quietly having transferred nothing must not read as success.
        var onDisk = bytesOnDisk();

        if (!InstanceCompleteness.Holds(onDisk, catalog.PublishedSizeBytes))
        {
            return new DlcDownloadResult(false,
                Strings.Current.Format("Core.Instances.DlcDownload.Incomplete",
                    InstanceCompleteness.Shortfall(onDisk, catalog.PublishedSizeBytes), dlcName));
        }

        return new DlcDownloadResult(true, null);
    }
}
