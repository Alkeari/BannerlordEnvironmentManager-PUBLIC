using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Nexus;

namespace BannerlordEnvironmentManager.Services
{
    // The only place in BEM that talks to Nexus.
    //
    // The key is attached to a single request message and never written anywhere else. Nothing in
    // this file logs, and nothing that does log is handed a request, a header collection or a URL
    // built from the key, because the key is never in a URL to begin with.
    //
    // The HttpClient is created on first use, so a BEM whose owner never enables any of this pays
    // nothing for it at startup.
    public sealed class NexusHttpTransport : INexusTransport, INexusPublicQuery
    {
        private const string GraphQlUrl = "https://api.nexusmods.com/v2/graphql";

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

        // No key header: the public API needs none, and a request that has no credential to attach
        // cannot leak one.
        public async Task<NexusHttpResponse> QueryAsync(string query, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlUrl)
            {
                Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { query }),
                    System.Text.Encoding.UTF8,
                    "application/json")
            };

            try
            {
                using var response = await Client.Value.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);

                return new NexusHttpResponse(
                    (int)response.StatusCode,
                    await response.Content.ReadAsStringAsync(timeout.Token),
                    new Dictionary<string, string>());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return NexusHttpResponse.Failed(Strings.Current.Format(
                    "Core.Nexus.Transport.Timeout",
                    RequestTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)));
            }
            catch (HttpRequestException ex)
            {
                return NexusHttpResponse.Failed(ex.Message);
            }
        }

        private const long ProgressStep = 1024 * 1024;

        private static readonly Lazy<HttpClient> Client = new(Create, isThreadSafe: true);

        public async Task<NexusHttpResponse> GetAsync(string path, NexusApiKey apiKey, CancellationToken cancellationToken)
        {
            for (var attempt = 0; ; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RequestTimeout);

                using var request = new HttpRequestMessage(HttpMethod.Get, NexusEndpoints.BaseUrl + path);
                request.Headers.TryAddWithoutValidation("apikey", apiKey.Reveal());

                try
                {
                    using var response = await Client.Value.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);

                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var header in response.Headers)
                        headers[header.Key] = string.Join(", ", header.Value);

                    return new NexusHttpResponse(
                        (int)response.StatusCode,
                        await response.Content.ReadAsStringAsync(timeout.Token),
                        headers);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    return NexusHttpResponse.Failed(Strings.Current.Format(
                        "Core.Nexus.Transport.Timeout",
                        RequestTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)));
                }
                catch (HttpRequestException ex)
                {
                    // A dropped connection mid-body is transient: report it only once the bounded
                    // retries are spent, never the first time and never forever.
                    if (attempt >= HttpRetryPolicy.MaxAttempts - 1)
                        return NexusHttpResponse.Failed(ex.Message);

                    await Task.Delay(HttpRetryPolicy.Backoff(attempt), cancellationToken);
                }
            }
        }

        // The content URL is already a pre-signed CDN link, so no header of BEM's is attached to it.
        // The file is written to a .part beside its destination and renamed only once complete, so an
        // interrupted download can never be mistaken for a finished archive by the folder watcher.
        public async Task<NexusDownloadOutcome> DownloadAsync(
            string contentUrl,
            string destinationPath,
            IProgress<NxmDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; ; attempt++)
            {
                var partial = destinationPath + ".part";
                long written = 0;

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, contentUrl);
                    using var response = await Client.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    if (!response.IsSuccessStatusCode)
                        return new NexusDownloadOutcome(false, Strings.Current.Format(
                            "Core.Nexus.Transport.Download.ServerStatus",
                            ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)));

                    // Content-Length is what lets a bar be drawn rather than a spinner. A server is free
                    // not to send it, and progress then says the total is unknown rather than guessing.
                    var expected = response.Content.Headers.ContentLength;

                    await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                    await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        long total = 0;
                        long reported = 0;
                        int read;

                        progress?.Report(new NxmDownloadProgress(0, expected));

                        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            total += read;

                            // Reporting every 80 KB chunk would post thousands of updates to the UI
                            // thread for one archive, which costs more than it tells the user.
                            if (total - reported < ProgressStep)
                                continue;

                            reported = total;
                            progress?.Report(new NxmDownloadProgress(total, expected));
                        }

                        progress?.Report(new NxmDownloadProgress(total, expected));

                        written = total;
                    }
                }
                catch (OperationCanceledException)
                {
                    TryDelete(partial);
                    return new NexusDownloadOutcome(false, Strings.Current["Core.Nexus.Transport.Download.Canceled"]);
                }
                catch (HttpRequestException) when (attempt < HttpRetryPolicy.MaxAttempts - 1
                                                   && !cancellationToken.IsCancellationRequested)
                {
                    // A CDN that drops the connection mid-body has written nothing usable to the
                    // .part, so it is discarded and the download is retried from the start.
                    TryDelete(partial);
                    await Task.Delay(HttpRetryPolicy.Backoff(attempt), cancellationToken);
                    continue;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
                {
                    TryDelete(partial);
                    return new NexusDownloadOutcome(
                        false, Strings.Current.Format("Core.Nexus.Transport.Download.Failed", ex.Message));
                }

                // The destination's extension was chosen before a single byte of the file existed, from
                // a CDN link that often carries no real filename at all. Now that the bytes are on disk,
                // what they actually are is checked before anything downstream ever opens them as
                // whatever the guess said.
                var finalDestination = CorrectedDestination(partial, destinationPath);

                // Outside the using, because this process still held an exclusive write handle on the
                // .part inside it and Windows refuses to rename a file its own caller has open. That is
                // the "the process cannot access the file because it is being used by another process"
                // the download failed with.
                //
                // Never overwrite: NxmDownload has already picked a name nothing is using, and a race
                // that beat it to that name must cost the download, not the user's existing archive.
                try
                {
                    File.Move(partial, finalDestination, overwrite: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The bytes are on disk. Saying "failed" and leaving no path would throw away a
                    // download that actually completed.
                    return new NexusDownloadOutcome(
                        false,
                        Strings.Current.Format("Core.Nexus.Transport.Download.NotMoved", ex.Message, partial),
                        partial,
                        written);
                }

                // Not localized: NxmDownload reads Path on success and never this message, so it is a
                // diagnostic value rather than something a user is ever shown.
                return new NexusDownloadOutcome(true, $"Saved {written} bytes.", finalDestination, written);
            }
        }

        // A mismatch only ever renames within the same folder BEM already staged the download in; it
        // never moves the file anywhere the caller did not ask for. FreePath is reused rather than
        // duplicated so a renamed candidate gets the same "add (2)" collision handling any other
        // download does.
        private static string CorrectedDestination(string partialPath, string requestedDestination)
        {
            var detected = ArchiveFormatSniffer.DetectExtension(partialPath);

            if (detected is null
                || string.Equals(detected, Path.GetExtension(requestedDestination), StringComparison.OrdinalIgnoreCase))
                return requestedDestination;

            var corrected = Path.ChangeExtension(requestedDestination, detected);
            var folder = Path.GetDirectoryName(corrected) ?? string.Empty;

            return NxmDownload.FreePath(folder, Path.GetFileName(corrected));
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        // The Acceptable Use Policy requires an application name and version on every request and
        // names blank or impersonating metadata as unacceptable use, so BEM identifies itself as
        // itself and never as another manager.
        private static HttpClient Create()
        {
            var version = typeof(NexusHttpTransport).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            client.DefaultRequestHeaders.Add("Application-Name", NexusEndpoints.ApplicationName);
            client.DefaultRequestHeaders.Add("Application-Version", version);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BannerlordEnvironmentManager", version));
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
                $"(Windows_NT {Environment.OSVersion.Version}; {(Environment.Is64BitProcess ? "x64" : "x86")})"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            return client;
        }
    }
}
