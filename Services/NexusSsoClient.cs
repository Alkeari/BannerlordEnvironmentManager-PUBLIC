using System.Net.WebSockets;
using System.Text;
using System.Globalization;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Nexus;

namespace BannerlordEnvironmentManager.Services
{
    // The only place in BEM that opens a socket to the Nexus sign-in service.
    //
    // ClientWebSocket is part of the base library, so this adds no package. Nothing here logs: the
    // frames carry the connection token and then the key itself, and neither may ever be written
    // down anywhere.
    public sealed class NexusSsoWebSocket : INexusSsoSocket
    {
        private const int MaxFrameBytes = 64 * 1024;

        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

        private readonly ClientWebSocket socket = new();

        public NexusSsoWebSocket()
        {
            var version = typeof(NexusSsoWebSocket).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

            // The acceptable use policy names blank or impersonating metadata as unacceptable use.
            // The reference client cannot set this, because a browser WebSocket forbids it; this one
            // can, so BEM identifies itself as itself here too.
            socket.Options.SetRequestHeader("User-Agent", $"BannerlordEnvironmentManager/{version}");
        }

        public async Task<NexusSsoSocketResult> ConnectAsync(Uri url, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);

            try
            {
                await socket.ConnectAsync(url, timeout.Token);
                return NexusSsoSocketResult.Connected;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return NexusSsoSocketResult.Failed(Strings.Current.Format(
                    "Core.Nexus.Sso.Timeout", ConnectTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)));
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or InvalidOperationException)
            {
                return NexusSsoSocketResult.Failed(ex.Message);
            }
        }

        public async Task<NexusSsoSocketResult> SendAsync(string message, CancellationToken cancellationToken)
        {
            try
            {
                await socket.SendAsync(
                    Encoding.UTF8.GetBytes(message),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);

                return NexusSsoSocketResult.Connected;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException or ObjectDisposedException)
            {
                return NexusSsoSocketResult.Failed(ex.Message);
            }
        }

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            var frame = new MemoryStream();

            try
            {
                while (true)
                {
                    var received = await socket.ReceiveAsync(buffer, cancellationToken);

                    if (received.MessageType == WebSocketMessageType.Close)
                        return null;

                    frame.Write(buffer, 0, received.Count);

                    // A frame this size is not the protocol, and reading it to the end would let the
                    // far side decide how much memory this process spends.
                    if (frame.Length > MaxFrameBytes)
                        return null;

                    if (received.EndOfMessage)
                        return Encoding.UTF8.GetString(frame.ToArray());
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException or ObjectDisposedException)
            {
                return null;
            }
            finally
            {
                frame.Dispose();
            }
        }

        public void Dispose()
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    socket.Abort();
            }
            catch (ObjectDisposedException)
            {
            }

            socket.Dispose();
        }
    }

    public sealed class NexusSsoWebSocketFactory : INexusSsoSocketFactory
    {
        public INexusSsoSocket Create() => new NexusSsoWebSocket();
    }

    public sealed class NexusSsoBrowser : INexusSsoBrowser
    {
        public bool TryOpen(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                // Deliberately not logged with the URL: it carries the session id for an approval
                // that is about to hand over a credential.
                LoggingService.LogException(ex, "Failed to open the Nexus sign-in page in a browser");
                return false;
            }
        }
    }
}
