using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

public enum NxmDownloadStatus
{
    Downloaded,
    DownloadedButNotPlaced,
    Forwarded,
    NoHandlerToForwardTo,
    NotOurLink,
    NoApiKey,
    NoDestinationFolder,
    Expired,
    Refused,
    Unreachable,
    Canceled,
    Failed
}

public sealed record NxmDownloadResult(NxmDownloadStatus Status, string Message, string? Path = null);

// A mod archive can be hundreds of megabytes, so how far along it is has to be answerable while it is
// running. The total is nullable because a server is free to answer without a Content-Length, and a
// bar drawn from a guessed total lies about progress.
public readonly record struct NxmDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Fraction =>
        TotalBytes is { } total && total > 0
            ? Math.Clamp((double)BytesReceived / total, 0, 1)
            : null;

    public string Describe() =>
        TotalBytes is { } total && total > 0
            ? Strings.Current.Format("Core.Nexus.NxmDownload.Progress.WithTotal", Size(BytesReceived), Size(total), $"{Fraction * 100:0}")
            : Strings.Current.Format("Core.Nexus.NxmDownload.Progress.UnknownTotal", Size(BytesReceived));

    private static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes} bytes"
    };
}

// Fetches one archive for one link the user clicked on the Nexus website, into the folder BEM
// already watches for archives. It is the whole reason the free-tier nxm grant exists: without the
// key and expiry minted by that click there is no programmatic route to a download URL at all.
//
// One file per click, nothing bulk, nothing unattended, and nothing is installed: the archive lands
// on disk and BEM's existing install flow takes it from there.
public sealed class NxmDownload(NexusClient client, INexusTransport transport)
{
    // Inside the archives folder so the final move is a rename, and named so the watcher and the
    // archive scan can both skip it.
    public const string StagingFolderName = ".bem-downloading";

    public async Task<NxmDownloadResult> RunAsync(
        NxmLink link,
        NexusApiKey? apiKey,
        string? destinationFolder,
        DateTimeOffset now,
        IProgress<NxmDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!link.IsBannerlordDownload)
            return new NxmDownloadResult(NxmDownloadStatus.NotOurLink, Strings.Current["Core.Nexus.NxmDownload.NotOurLink"]);

        // The folder is checked before the key, deliberately: on a fresh machine both are missing,
        // and checking the key first walked a new user into two failures in a row - add the key, click
        // again, then learn the folder was missing too. Everything wrong with the setup that can be
        // known without a request is said in the first answer.
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            return new NxmDownloadResult(NxmDownloadStatus.NoDestinationFolder,
                apiKey is null
                    ? Strings.Current["Core.Nexus.NxmDownload.NoDestinationFolder.NoKeyEither"]
                    : Strings.Current["Core.Nexus.NxmDownload.NoDestinationFolder"]);
        }

        if (apiKey is not { } key)
            return new NxmDownloadResult(NxmDownloadStatus.NoApiKey,
                Strings.Current["Core.Nexus.NxmDownload.NoApiKey"]);

        // The expiry is in the link, so a dead grant is seen without spending a request on it.
        if (link.HasExpired(now))
            return new NxmDownloadResult(NxmDownloadStatus.Expired,
                Strings.Current["Core.Nexus.NxmDownload.Expired"]);

        var urls = await client.GetDownloadUrlsAsync(key, link, cancellationToken);

        if (!urls.IsOk || urls.Value is not { Count: > 0 } locations)
            return new NxmDownloadResult(urls.Outcome switch
            {
                NexusOutcome.Unauthorized => NxmDownloadStatus.Refused,
                NexusOutcome.Unreachable => NxmDownloadStatus.Unreachable,
                NexusOutcome.Canceled => NxmDownloadStatus.Canceled,
                _ => NxmDownloadStatus.Failed
            }, urls.Message);

        var staging = Path.Combine(destinationFolder, StagingFolderName);

        try
        {
            Directory.CreateDirectory(destinationFolder);
            Directory.CreateDirectory(staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new NxmDownloadResult(NxmDownloadStatus.Failed, Strings.Current.Format("Core.Nexus.NxmDownload.FolderNotCreated", ex.Message));
        }

        var fileName = FileNameFrom(locations[0].Uri, link);
        var destination = FreePath(destinationFolder, fileName);

        // Downloading straight into the watched folder puts a growing file under the archive watcher,
        // which opens whatever it sees. The download is assembled here instead and arrives in the
        // watched folder as one already-complete file. A subfolder rather than the system temp
        // folder, so the final move is always a rename on the same volume and never a copy of several
        // hundred megabytes.
        var assembled = FreePath(staging, fileName);

        var outcome = await transport.DownloadAsync(locations[0].Uri, assembled, progress, cancellationToken);

        if (!outcome.Ok)
            return outcome.Path is { Length: > 0 } stranded
                ? new NxmDownloadResult(NxmDownloadStatus.DownloadedButNotPlaced, outcome.Message, stranded)
                : new NxmDownloadResult(NxmDownloadStatus.Failed, outcome.Message);

        var downloaded = outcome.Path ?? assembled;

        // The destination was named before the download existed, from a link that often carries no
        // filename at all. The transport has since read the file's own signature and may hand back a
        // corrected name, and moving that back onto the guessed extension threw the correction away:
        // a 7z arrived in the watched folder called ".zip" and the installer, picking its reader from
        // the name, failed on it with "End of Central Directory record could not be found."
        var placed = CarryCorrectedExtension(downloaded, destination, destinationFolder);

        try
        {
            File.Move(downloaded, placed, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The bytes are on disk and paid for. Saying "failed" and not saying where they are is
            // the worst answer available here.
            return new NxmDownloadResult(NxmDownloadStatus.DownloadedButNotPlaced,
                Strings.Current.Format("Core.Nexus.NxmDownload.NotMoved", destinationFolder, ex.Message, downloaded),
                downloaded);
        }

        return new NxmDownloadResult(NxmDownloadStatus.Downloaded,
            Strings.Current.Format("Core.Nexus.NxmDownload.Downloaded", Path.GetFileName(placed), destinationFolder), placed);
    }

    // FreePath is reused rather than duplicated so a corrected name collides the way any other name
    // does, and an archive the user already has is never written over by one.
    private static string CarryCorrectedExtension(string downloaded, string destination, string destinationFolder)
    {
        var actual = Path.GetExtension(downloaded);

        if (string.Equals(actual, Path.GetExtension(destination), StringComparison.OrdinalIgnoreCase))
            return destination;

        return FreePath(destinationFolder, Path.GetFileName(Path.ChangeExtension(destination, actual)));
    }

    // An archive already sitting in the watched folder is the user's, and a second click on the same
    // file must never destroy it: the same name that Windows Explorer would produce is used instead.
    // The .part the transport writes beside the destination is checked too, so a download already in
    // flight is not written over either.
    public static string FreePath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);

        if (IsFree(candidate))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var attempt = 2; attempt < 1000; attempt++)
        {
            candidate = Path.Combine(folder, $"{stem} ({attempt}){extension}");

            if (IsFree(candidate))
                return candidate;
        }

        return Path.Combine(folder, $"{stem} ({DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}){extension}");
    }

    private static bool IsFree(string path) => !File.Exists(path) && !File.Exists(path + ".part");

    // The CDN link's last path segment is the Nexus filename, which is what BEM's archive watcher and
    // its mod-id parser both expect. The mod and file ids from the link are the fallback, because
    // they are authoritative and the filename is not.
    public static string FileNameFrom(string contentUrl, NxmLink link)
    {
        var fallback = $"nexus-{link.ModId}-{link.FileId}.zip";

        if (!Uri.TryCreate(contentUrl, UriKind.Absolute, out var uri))
            return fallback;

        var segment = uri.Segments.LastOrDefault() ?? string.Empty;

        if (segment.EndsWith('/'))
            return fallback;

        var name = Uri.UnescapeDataString(segment);

        return string.IsNullOrWhiteSpace(name)
            || !Path.HasExtension(name)
            || name.Any(character => Path.GetInvalidFileNameChars().Contains(character))
                ? fallback
                : name;
    }
}
