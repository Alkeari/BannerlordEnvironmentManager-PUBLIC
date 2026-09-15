using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

public enum ModUpdateDownloadStatus
{
    Succeeded,
    NeedsPremium,
    NotFound,
    NoApiKey,
    NoDestinationFolder,
    Unreachable,
    RateLimited,
    Canceled,
    Failed
}

public sealed record ModUpdateDownloadResult(ModUpdateDownloadStatus Status, string Message, string? ArchivePath = null);

// Downloads the newer file Nexus published for a module the user is updating, into a staging folder
// BEM then installs from. It is autonomous, so it goes through Nexus's bare download_link endpoint,
// which Nexus serves only to the archive's author and to Premium accounts: that is what NeedsPremium
// reports. A free user still updates by hand through the NXM link, which BEM already handles.
//
// The archive is kept out of the watched archives folder until it is installed, so the watcher does
// not also try to add it as a brand new mod on top of the update.
public sealed class ModUpdateDownloader(NexusClient client, INexusTransport transport)
{
    public const string StagingFolderName = ".bem-updating";

    public async Task<ModUpdateDownloadResult> DownloadAsync(
        int modId,
        int fileId,
        NexusApiKey? apiKey,
        string destinationFolder,
        IProgress<NxmDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (apiKey is not { } key)
            return new ModUpdateDownloadResult(ModUpdateDownloadStatus.NoApiKey,
                Strings.Current["Core.Nexus.ModUpdateDownload.NoApiKey"]);

        if (string.IsNullOrWhiteSpace(destinationFolder))
            return new ModUpdateDownloadResult(ModUpdateDownloadStatus.NoDestinationFolder,
                Strings.Current["Core.Nexus.ModUpdateDownload.NoDestinationFolder"]);

        // No download key and no expiry: the bare endpoint Nexus reserves for the file's author and
        // for Premium accounts. Anything else is answered as NeedsPremium rather than guessed from a
        // message, because 401 and 403 are the same statement here.
        var urls = await client.GetDownloadUrlsAsync(
            key,
            new NxmLink(NxmLinkKind.BannerlordMod, string.Empty, NexusEndpoints.GameDomain, modId, fileId),
            cancellationToken);

        if (!urls.IsOk || urls.Value is not { Count: > 0 } locations)
            return new ModUpdateDownloadResult(urls.Outcome switch
            {
                NexusOutcome.Unauthorized => ModUpdateDownloadStatus.NeedsPremium,
                NexusOutcome.NotFound => ModUpdateDownloadStatus.NotFound,
                NexusOutcome.Unreachable => ModUpdateDownloadStatus.Unreachable,
                NexusOutcome.RateLimited => ModUpdateDownloadStatus.RateLimited,
                NexusOutcome.Canceled => ModUpdateDownloadStatus.Canceled,
                _ => ModUpdateDownloadStatus.Failed
            }, urls.Message);

        var staging = Path.Combine(destinationFolder, StagingFolderName);

        try
        {
            Directory.CreateDirectory(destinationFolder);
            Directory.CreateDirectory(staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ModUpdateDownloadResult(ModUpdateDownloadStatus.Failed,
                Strings.Current.Format("Core.Nexus.ModUpdateDownload.FolderNotCreated", ex.Message));
        }

        var fileName = FileNameFrom(locations[0].Uri, modId, fileId);
        var destination = NxmDownload.FreePath(staging, fileName);

        var outcome = await transport.DownloadAsync(locations[0].Uri, destination, progress, cancellationToken);

        if (!outcome.Ok)
            return outcome.Path is { Length: > 0 } stranded
                ? new ModUpdateDownloadResult(ModUpdateDownloadStatus.Failed,
                    Strings.Current.Format("Core.Nexus.ModUpdateDownload.NotFinalized", outcome.Message, stranded),
                    stranded)
                : new ModUpdateDownloadResult(ModUpdateDownloadStatus.Failed, outcome.Message);

        var downloaded = outcome.Path ?? destination;

        return new ModUpdateDownloadResult(ModUpdateDownloadStatus.Succeeded,
            Strings.Current.Format("Core.Nexus.ModUpdateDownload.Downloaded", Path.GetFileName(downloaded)), downloaded);
    }

    // The CDN link's last path segment is the Nexus filename; the ids are the fallback because the
    // filename is not a contract.
    private static string FileNameFrom(string contentUrl, int modId, int fileId)
    {
        var fallback = $"nexus-{modId}-{fileId}.zip";

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
