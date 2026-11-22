namespace BannerlordEnvironmentManager.Core.Instances;

// Whether a download that ended early ended because the user asked it to.
//
// Stopping a download is a thing the user did, not a thing that went wrong, and the difference has
// to be readable in one place rather than decided again in every catch block: a stopped download is
// reported as stopped, is never retried, and leaves a folder Unfinished Downloads can carry on from.
//
// The exception type alone cannot answer it. HttpClient's own timeout raises TaskCanceledException,
// which is an OperationCanceledException, and a run that timed out fetching DepotDownloader failed
// rather than being stopped. The token BEM would have canceled is what settles it.
public static class DownloadCancellation
{
    public static bool WasStoppedByUser(Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
    }
}
