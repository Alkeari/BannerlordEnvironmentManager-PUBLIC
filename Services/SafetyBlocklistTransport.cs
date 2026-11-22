using System.Net.Http;
using System.Net.Http.Headers;
using BannerlordEnvironmentManager.Core.Safety;

namespace BannerlordEnvironmentManager.Services
{
    // The only place in BEM that reads the community blocklist.
    //
    // One anonymous GET of one public file: no key, no token, no account, and nothing about this
    // machine leaves it beyond the request itself. The file is Calradia Warden's own published list,
    // which is where the reports are collected. The HttpClient is created on first use, so a BEM with
    // this switched off pays nothing for it.
    public static class SafetyBlocklistTransport
    {
        public const string Url =
            "https://raw.githubusercontent.com/mazetankzz-gif/calradia-warden/main/blocklist.json";

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

        private static readonly Lazy<HttpClient> Client = new(Create, isThreadSafe: true);

        public static async Task<string?> FetchAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var response = await Client.Value.GetAsync(Url, timeout.Token);

            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(timeout.Token) : null;
        }

        private static HttpClient Create()
        {
            var version = typeof(SafetyBlocklistTransport).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BannerlordEnvironmentManager", version));

            return client;
        }
    }
}
