using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Instances;

// One DLC's download, reduced to whether it worked and, if not, what Steam said. A production
// caller reads this off a DepotDownloaderTool run the same way the base download already does
// (the last progress reported before the process exited); this type only carries the answer, so
// the flow that sequences a variant's DLC downloads never has to touch a process itself, which is
// what lets it be tested without a network or DepotDownloader.
public sealed record DlcDownloadResult(bool Succeeded, string? FailureMessage);

// What a variant's DLC downloads did, in the order they were attempted. Downloaded is every app id
// that finished before the first failure, or the whole variant if none failed; a caller records
// exactly this, never the variant that was requested, so a partial or failed run is never written
// down as more than what actually landed.
public sealed record DlcDownloadOutcome(GameDlcSet Downloaded, int? FailedAppId, string? FailureMessage)
{
    public bool Succeeded => FailureMessage is null;

    public static DlcDownloadOutcome Empty { get; } = new(GameDlcSet.Empty, null, null);
}

// After the base game is on disk, one further download per DLC the requested variant carries, into
// the same game folder, in the stable order GameDlcSet already keeps (sorted by app id). Stops at
// the first failure rather than attempting the rest: a second DLC download after Steam already
// refused one answers a question nobody asked, and the caller is left to report exactly one
// failure rather than a pile of them.
//
// downloadOne is the seam: production hands in a delegate that runs a real DepotDownloaderTool
// invocation and reads its last progress, a test hands in a delegate that returns canned results
// and records which app ids it was asked for, in what order. Nothing here starts a process,
// touches a socket, or knows DepotDownloader exists.
public static class DlcDownloadFlow
{
    public static async Task<DlcDownloadOutcome> RunAsync(
        GameDlcSet variant,
        Func<int, CancellationToken, Task<DlcDownloadResult>> downloadOne,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downloadOne);

        if (variant.IsEmpty)
            return DlcDownloadOutcome.Empty;

        var downloaded = new List<int>();

        foreach (var appId in variant.AppIds)
        {
            var result = await downloadOne(appId, cancellationToken);

            if (!result.Succeeded)
            {
                return new DlcDownloadOutcome(
                    new GameDlcSet(downloaded),
                    appId,
                    result.FailureMessage is { Length: > 0 } message ? message : Strings.Current["Core.Instances.DepotDownloader.ExitedWithError"]);
            }

            downloaded.Add(appId);
        }

        return new DlcDownloadOutcome(new GameDlcSet(downloaded), null, null);
    }
}
