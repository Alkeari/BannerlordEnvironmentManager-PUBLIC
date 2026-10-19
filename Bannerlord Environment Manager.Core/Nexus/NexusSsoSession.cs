using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

public enum NexusSsoStatus
{
    NotEnabled,
    KeyReceived,
    KeyNotStored,
    Unreachable,
    Declined,
    ApplicationRefused,
    Refused,
    TimedOut,
    Dropped,
    Canceled
}

public sealed record NexusSsoResult(NexusSsoStatus Status, string Message, NexusApiKey? Key = null)
{
    public bool Succeeded => Status == NexusSsoStatus.KeyReceived;
}

public sealed record NexusSsoRequest(
    string ApplicationSlug,
    TimeSpan ApprovalTimeout,
    int MaxReconnectAttempts = 5)
{
    public static NexusSsoRequest Default { get; } = new(NexusSso.ApplicationSlug, TimeSpan.FromMinutes(5));
}

// The client for the protocol, written to behave as a real working client does: one opening
// frame carrying the session id and any stored connection token, the browser sent to the approval
// page for that id, then the key delivered over the same socket. It reconnects on a drop and resumes
// with the stored token rather than asking for a second approval.
public sealed class NexusSsoSession
{
    private readonly INexusSsoSocketFactory sockets;

    private readonly INexusSsoBrowser browser;

    private readonly NexusSsoTokenStore tokenStore;

    private readonly NexusApiKeyStore keyStore;

    private readonly Func<string> newSessionId;

    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public NexusSsoSession(
        INexusSsoSocketFactory sockets,
        INexusSsoBrowser browser,
        NexusSsoTokenStore tokenStore,
        NexusApiKeyStore keyStore,
        Func<string>? newSessionId = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.sockets = sockets;
        this.browser = browser;
        this.tokenStore = tokenStore;
        this.keyStore = keyStore;
        this.newSessionId = newSessionId ?? (() => Guid.NewGuid().ToString());
        this.delay = delay ?? Task.Delay;
    }

    public async Task<NexusSsoResult> RunAsync(
        NexusSsoRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ApplicationSlug))
            return new NexusSsoResult(
                NexusSsoStatus.NotEnabled,
                Strings.Current["Core.Nexus.Sso.NotEnabled"]);

        var tokens = tokenStore.Load() ?? new NexusSsoTokens(newSessionId());
        var approvalUrl = NexusSso.ApprovalUrl(tokens.SessionId, request.ApplicationSlug);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.ApprovalTimeout);

        var attempt = 0;

        try
        {
            while (true)
            {
                var (result, carried) = await AttemptAsync(tokens, approvalUrl, attempt, progress, deadline.Token);

                if (result is not null)
                    return result;

                tokens = carried;

                if (attempt >= request.MaxReconnectAttempts)
                    return new NexusSsoResult(
                        NexusSsoStatus.Dropped,
                        Strings.Current.Format("Core.Nexus.Sso.Dropped", request.MaxReconnectAttempts));

                attempt++;
                progress?.Report(Strings.Current.Format("Core.Nexus.Sso.Progress.Reconnecting", attempt, request.MaxReconnectAttempts));

                await delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), deadline.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? new NexusSsoResult(NexusSsoStatus.Canceled, Strings.Current["Core.Nexus.Sso.Canceled"])
                : new NexusSsoResult(
                    NexusSsoStatus.TimedOut,
                    Strings.Current["Core.Nexus.Sso.TimedOut"]);
        }
    }

    // A null result means the socket went away and the caller decides whether there is another
    // attempt left. Every other ending is final and says which ending it was.
    private async Task<(NexusSsoResult? Result, NexusSsoTokens Tokens)> AttemptAsync(
        NexusSsoTokens tokens,
        string approvalUrl,
        int attempt,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var socket = sockets.Create();

        if (attempt == 0)
            progress?.Report(Strings.Current["Core.Nexus.Sso.Progress.Connecting"]);

        var connected = await socket.ConnectAsync(new Uri(NexusSso.WebSocketUrl), cancellationToken);

        if (!connected.Ok)
            return (attempt == 0 ? Unreachable(connected.Error) : null, tokens);

        var sent = await socket.SendAsync(Handshake(tokens), cancellationToken);

        if (!sent.Ok)
            return (attempt == 0 ? Unreachable(sent.Error) : null, tokens);

        // The browser is opened once. A second tab on a reconnect reads as the first approval not
        // having worked, which is exactly what the stored connection token exists to avoid.
        if (attempt == 0)
        {
            var opened = browser.TryOpen(approvalUrl);

            progress?.Report(opened
                ? Strings.Current["Core.Nexus.Sso.Progress.ApproveInBrowser"]
                : Strings.Current.Format("Core.Nexus.Sso.Progress.BrowserNotOpened", approvalUrl));
        }
        else
        {
            progress?.Report(Strings.Current["Core.Nexus.Sso.Progress.ReconnectedWaiting"]);
        }

        while (true)
        {
            var frame = await socket.ReceiveAsync(cancellationToken);

            if (frame is null)
                return (null, tokens);

            var message = Parse(frame);

            if (!message.Understood)
            {
                tokenStore.Clear();

                return (new NexusSsoResult(
                    NexusSsoStatus.Refused,
                    Strings.Current["Core.Nexus.Sso.NotUnderstood"]), tokens);
            }

            if (message.Error is { } error)
            {
                tokenStore.Clear();
                return (Refusal(error), tokens);
            }

            if (message.ConnectionToken is { } connectionToken)
            {
                tokens = tokens with { ConnectionToken = connectionToken };
                tokenStore.Save(tokens);
                progress?.Report(Strings.Current["Core.Nexus.Sso.Progress.ReadyWaiting"]);
            }

            if (message.ApiKey is { } apiKey)
            {
                tokenStore.Clear();
                return (Complete(apiKey), tokens);
            }
        }
    }

    private NexusSsoResult Complete(string apiKey)
    {
        if (NexusApiKey.TryCreate(apiKey) is not { } key)
            return new NexusSsoResult(
                NexusSsoStatus.KeyNotStored,
                Strings.Current["Core.Nexus.Sso.KeyNotUsable"]);

        // The one store every Nexus request reads the key from, whose round-trip check refuses a key
        // it could not read back.
        var status = keyStore.Save(key);

        return status.State == NexusApiKeyState.Available
            ? new NexusSsoResult(NexusSsoStatus.KeyReceived, Strings.Current.Format("Core.Nexus.Sso.SignedIn", status.Message), key)
            : new NexusSsoResult(NexusSsoStatus.KeyNotStored, status.Message);
    }

    private static NexusSsoResult Unreachable(string? error) =>
        new(NexusSsoStatus.Unreachable,
            Strings.Current.Format("Core.Nexus.Sso.Unreachable", error ?? Strings.Current["Core.Nexus.Sso.NoReasonGiven"]));

    // Nexus does not publish the wording of these errors, so the server's own words are always
    // repeated verbatim. Reading them wrong here changes only which headline is shown, never the
    // facts the user is given.
    private static NexusSsoResult Refusal(string error) =>
        error.Contains("application", StringComparison.OrdinalIgnoreCase)
        || error.Contains("slug", StringComparison.OrdinalIgnoreCase)
            ? new NexusSsoResult(
                NexusSsoStatus.ApplicationRefused,
                Strings.Current.Format("Core.Nexus.Sso.ApplicationRefused", error))
            : error.Contains("deni", StringComparison.OrdinalIgnoreCase)
              || error.Contains("declin", StringComparison.OrdinalIgnoreCase)
              || error.Contains("cancel", StringComparison.OrdinalIgnoreCase)
                ? new NexusSsoResult(
                    NexusSsoStatus.Declined,
                    Strings.Current.Format("Core.Nexus.Sso.Declined", error))
                : new NexusSsoResult(
                    NexusSsoStatus.Refused,
                    Strings.Current.Format("Core.Nexus.Sso.Refused", error));

    private static string Handshake(NexusSsoTokens tokens) =>
        JsonSerializer.Serialize(new SsoHandshake(tokens.SessionId, tokens.ConnectionToken, NexusSso.Protocol));

    private static SsoMessage Parse(string frame)
    {
        try
        {
            using var document = JsonDocument.Parse(frame);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return new SsoMessage(false, null, null, null);

            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                return new SsoMessage(true, Text(root, "error") ?? Strings.Current["Core.Nexus.Sso.NoReasonGiven"], null, null);

            return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                ? new SsoMessage(true, null, Text(data, "connection_token"), Text(data, "api_key"))
                : new SsoMessage(true, null, null, null);
        }
        catch (JsonException)
        {
            return new SsoMessage(false, null, null, null);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record SsoHandshake(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("protocol")] int Protocol);

    private sealed record SsoMessage(bool Understood, string? Error, string? ConnectionToken, string? ApiKey);
}
