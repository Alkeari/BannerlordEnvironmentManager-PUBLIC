namespace BannerlordEnvironmentManager.Core.Nexus;

public sealed record NexusHttpResponse(
    int StatusCode,
    string Body,
    IReadOnlyDictionary<string, string> Headers,
    string? TransportError = null)
{
    private static readonly Dictionary<string, string> NoHeaders = [];

    // A request that never arrived is a different answer from one that arrived and said no. Status
    // zero exists so no caller can mistake the two.
    public static NexusHttpResponse Failed(string error) =>
        new(0, string.Empty, NoHeaders, error);

    public bool IsSuccess => TransportError is null && StatusCode is >= 200 and < 300;
}

public sealed record NexusDownloadOutcome(bool Ok, string Message, string? Path = null, long Bytes = 0);

// The seam that keeps Core free of HttpClient and free of the network. The key is a parameter rather
// than part of the path so that no implementation can put it in a URL by accident.
public interface INexusTransport
{
    Task<NexusHttpResponse> GetAsync(string path, NexusApiKey apiKey, CancellationToken cancellationToken);

    // The content URL is a pre-signed CDN link that carries its own grant, so this call sends no
    // credential of BEM's at all.
    Task<NexusDownloadOutcome> DownloadAsync(
        string contentUrl,
        string destinationPath,
        IProgress<NxmDownloadProgress>? progress,
        CancellationToken cancellationToken);
}
