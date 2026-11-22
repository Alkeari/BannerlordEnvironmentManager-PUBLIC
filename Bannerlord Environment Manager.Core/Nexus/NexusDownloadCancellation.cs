namespace BannerlordEnvironmentManager.Core.Nexus;

// Which Nexus download a stop is about.
//
// Every download used to run on one CancellationTokenSource that each new download canceled and
// replaced, which made two independent activities into one. A Mod Manager Download the user clicked
// during a guided BUTR Stack walk canceled the walk's own transfer, and the walk then wrote that mod
// down as failed without it ever having been attempted. A download now owns its token for as long as
// it runs, so stopping one stops exactly one.
//
// Current is whichever download started last and has not finished, because that is the one writing
// the progress the page is showing and so the one the Cancel button beside it is about. A download
// that is no longer current is still running and still stoppable: through the token its own caller
// started it with, which is how the walk stops its transfer when the walk is stopped.
public sealed class NexusDownloadCancellation
{
    private readonly Lock gate = new();
    private readonly HashSet<NexusDownload> running = [];
    private NexusDownload? current;

    public NexusDownload Start(CancellationToken cancellationToken = default)
    {
        var download = new NexusDownload(this, cancellationToken);

        lock (gate)
        {
            running.Add(download);
            current = download;
        }

        return download;
    }

    // The Cancel button on the download bar, which is beside the progress the current download writes.
    public void CancelCurrent()
    {
        NexusDownload? download;

        lock (gate)
            download = current;

        download?.Cancel();
    }

    // Teardown: the page is going away, so every transfer it started goes with it.
    public void CancelAll()
    {
        NexusDownload[] downloads;

        lock (gate)
            downloads = [.. running];

        foreach (var download in downloads)
            download.Cancel();
    }

    internal bool IsCurrent(NexusDownload download)
    {
        lock (gate)
            return ReferenceEquals(current, download);
    }

    internal void Finished(NexusDownload download)
    {
        lock (gate)
        {
            running.Remove(download);

            if (ReferenceEquals(current, download))
                current = null;
        }
    }
}

// One download's own cancellation, for as long as that download runs. Disposing it ends the handle
// and not the download: the download is over by then, and the caller that started it is the one that
// says so.
public sealed class NexusDownload : IDisposable
{
    private readonly NexusDownloadCancellation owner;
    private readonly CancellationTokenSource source;
    private bool finished;

    internal NexusDownload(NexusDownloadCancellation owner, CancellationToken cancellationToken)
    {
        this.owner = owner;
        source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Read once rather than on demand: a disposed source cannot be asked for its token, while the
        // token itself stays usable and reads as canceled.
        Token = source.Token;
    }

    public CancellationToken Token { get; }

    // Whether this is the download the page's bar and its Cancel button are about. A download that a
    // later one has taken the bar from finishes silently rather than writing its outcome over the
    // newer download's.
    public bool IsCurrent => owner.IsCurrent(this);

    public void Cancel()
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            _ = ex;
        }
    }

    public void Dispose()
    {
        if (finished)
            return;

        finished = true;
        owner.Finished(this);
        source.Dispose();
    }
}
