using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Globalization;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Butr;

namespace BannerlordEnvironmentManager.Services
{
    // The only place in BEM that sends a crash report to BUTR. It runs when the user has agreed to
    // an upload of a payload they have read, and at no other time. The request itself is Core's
    // ButrCrashUploader, which a test drives with a fake handler.
    public sealed class ButrCrashHttpTransport : IButrCrashTransport
    {
        private static readonly ButrCrashUploader Uploader = new();

        public Task<ButrUploadResponse> UploadAsync(string json, CancellationToken cancellationToken) =>
            Uploader.UploadAsync(json, cancellationToken);
    }

    // The only place in BEM that talks to the BUTR community service.
    //
    // The call is anonymous: no key, no token, no account, nothing that identifies the user. What
    // leaves the machine is the list of module ids and versions plus the game version, which is the
    // question being asked.
    //
    // The HttpClient is created on first use, so a BEM with this switched off pays nothing for it.
    public sealed class ButrHttpTransport : IButrTransport
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

        private static readonly Lazy<HttpClient> Client = new(Create, isThreadSafe: true);

        public async Task<ButrHttpResponse> PostJsonAsync(string path, string body, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, ButrEndpoints.BaseUrl + path)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            request.Headers.TryAddWithoutValidation(ButrEndpoints.TenantHeaderName, ButrEndpoints.BannerlordTenant);

            try
            {
                using var response = await Client.Value.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);

                return new ButrHttpResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return ButrHttpResponse.Failed(
                        Strings.Current.Format(
                        "Core.Butr.Transport.Timeout", RequestTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)));
            }
            catch (HttpRequestException ex)
            {
                return ButrHttpResponse.Failed(ex.Message);
            }
        }

        private static HttpClient Create()
        {
            var version = typeof(ButrHttpTransport).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BannerlordEnvironmentManager", version));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            return client;
        }
    }

    // The only place in BEM that sends a credential to BUTR, and it runs only when the user presses
    // the button that says so. The Nexus API key is exchanged for a BUTR token on the first call and
    // every call after it carries the token instead, which is the flow BUTR's own clients use.
    //
    // The timeout is generous because the index is read a page at a time and the whole Bannerlord
    // library is a lot of rows.
    public sealed class ButrAuthenticatedHttpTransport : IButrAuthenticatedTransport
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

        private static readonly Lazy<HttpClient> Client = new(Create, isThreadSafe: true);

        public async Task<ButrHttpResponse> PostJsonAsync(ButrAuthenticatedRequest request, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var message = new HttpRequestMessage(HttpMethod.Post, ButrEndpoints.BaseUrl + request.Path)
            {
                Content = new StringContent(request.Body, Encoding.UTF8, "application/json")
            };

            message.Headers.TryAddWithoutValidation(ButrEndpoints.TenantHeaderName, ButrEndpoints.BannerlordTenant);

            foreach (var header in request.Headers)
            {
                message.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }

            try
            {
                using var response = await Client.Value.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token);

                return new ButrHttpResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return ButrHttpResponse.Failed(
                        Strings.Current.Format(
                        "Core.Butr.Transport.Timeout", RequestTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)));
            }
            catch (HttpRequestException ex)
            {
                return ButrHttpResponse.Failed(ex.Message);
            }
        }

        private static HttpClient Create()
        {
            var version = typeof(ButrAuthenticatedHttpTransport).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BannerlordEnvironmentManager", version));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            return client;
        }
    }
}
