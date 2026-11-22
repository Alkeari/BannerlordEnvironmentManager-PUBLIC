using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Butr;

// The request ButterLib's own BUTRCrashUploader makes: POST, a gzip JSON body, the Tenant header the
// server binds its partition from, and the report version it picks its reader by. The answer is the
// report's URL as plain text, and since the server added delete tokens it also carries the URL that
// removes the report in the X-Delete-Url response header.
//
// Takes the handler so a test can stand in for the service; nothing in a test ever reaches it.
public sealed class ButrCrashUploader(
    HttpMessageHandler? handler = null,
    string url = ButrEndpoints.CrashUploadUrl,
    TimeSpan? timeout = null) : IButrCrashTransport
{
    public const string DeleteUrlHeaderName = "X-Delete-Url";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly Lazy<HttpClient> client = new(() => Create(handler), isThreadSafe: true);

    private TimeSpan RequestTimeout => timeout ?? DefaultTimeout;

    public async Task<ButrUploadResponse> UploadAsync(string json, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(RequestTimeout);

        using var content = new ByteArrayContent(Compress(json));

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        content.Headers.ContentEncoding.Add("deflate");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

        request.Headers.TryAddWithoutValidation(ButrEndpoints.TenantHeaderName, ButrEndpoints.BannerlordTenant);
        request.Headers.TryAddWithoutValidation(
            ButrEndpoints.ReportVersionHeaderName,
            ButrCrashPayload.ReportVersion.ToString(CultureInfo.InvariantCulture));

        try
        {
            using var response = await client.Value.SendAsync(request, HttpCompletionOption.ResponseContentRead, limit.Token);

            var deleteUrl = response.Headers.TryGetValues(DeleteUrlHeaderName, out var values)
                ? values.FirstOrDefault()
                : null;

            return new ButrUploadResponse(
                (int)response.StatusCode,
                await response.Content.ReadAsStringAsync(limit.Token),
                DeleteUrl: deleteUrl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ButrUploadResponse.Failed(
                Strings.Current.Format(
                    "Core.Butr.Transport.Timeout", RequestTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)));
        }
        catch (HttpRequestException ex)
        {
            return ButrUploadResponse.Failed(ex.Message);
        }
    }

    private static byte[] Compress(string json)
    {
        using var stream = new MemoryStream();

        using (var gzip = new GZipStream(stream, CompressionMode.Compress, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return stream.ToArray();
    }

    private static HttpClient Create(HttpMessageHandler? handler)
    {
        var version = typeof(ButrCrashUploader).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        var created = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);

        created.Timeout = Timeout.InfiniteTimeSpan;
        created.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BannerlordEnvironmentManager", version));

        return created;
    }
}
