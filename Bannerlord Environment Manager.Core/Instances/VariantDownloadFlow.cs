namespace BannerlordEnvironmentManager.Core.Instances;

// What a variant download did: whether the base game landed, and, only if it did, what its DLC did.
public sealed record VariantDownloadOutcome(bool BaseSucceeded, DlcDownloadOutcome Dlc)
{
    public static VariantDownloadOutcome BaseFailed { get; } = new(false, DlcDownloadOutcome.Empty);
}

// The one place that decides a variant's DLC is never attempted before the base game finished, and
// never attempted at all when the base game did not. Before this existed, that ordering was nothing
// more than which statement came first inside an untested view model method - true today, and true
// only for as long as nobody reorders two lines. downloadBase and downloadOne are delegates so the
// decision can be tested with fakes that record what was called and in what order, without a
// process, a network call, or a UI dialog anywhere near the test.
public static class VariantDownloadFlow
{
    public static async Task<VariantDownloadOutcome> RunAsync(
        Func<CancellationToken, Task<bool>> downloadBase,
        GameDlcSet variant,
        Func<int, CancellationToken, Task<DlcDownloadResult>> downloadOne,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downloadBase);
        ArgumentNullException.ThrowIfNull(downloadOne);

        if (!await downloadBase(cancellationToken))
            return VariantDownloadOutcome.BaseFailed;

        var dlc = await DlcDownloadFlow.RunAsync(variant, downloadOne, cancellationToken);

        return new VariantDownloadOutcome(true, dlc);
    }
}
