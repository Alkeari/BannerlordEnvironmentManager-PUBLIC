namespace BannerlordEnvironmentManager.Core.Nexus;

// The single sign-on protocol Nexus exposes, and the only way BEM gets a key: click sign in, approve
// in the browser. Nexus grants single sign-on only to an application that does not also take
// personal API keys, so there is no paste to fall back on.
public static class NexusSso
{
    public const string WebSocketUrl = "wss://sso.nexusmods.com";

    public const string ApprovalPageUrl = "https://www.nexusmods.com/sso";

    // Version 2 is what the protocol expects in the opening frame. Sending anything else is refused.
    public const int Protocol = 2;

    // PENDING NEXUS APPROVAL. Nexus issues an application slug only after it has reviewed and
    // approved the application, and BEM's application has not been submitted and approved yet.
    //
    // It stays empty until Nexus issues one, and filling it in is the whole switch-on: every other
    // part of the sign-in is built and tested. Do not invent, guess or copy a value here. A slug
    // Nexus did not issue for BEM is refused by the service, and using another application's slug
    // would sign the user in to that application.
    public const string ApplicationSlug = "";

    public static bool IsEnabled => !string.IsNullOrWhiteSpace(ApplicationSlug);

    public static string ApprovalUrl(string sessionId, string applicationSlug) =>
        $"{ApprovalPageUrl}?id={Uri.EscapeDataString(sessionId)}&application={Uri.EscapeDataString(applicationSlug)}";
}

public sealed record NexusSsoSocketResult(bool Ok, string? Error = null)
{
    public static NexusSsoSocketResult Connected { get; } = new(true);

    public static NexusSsoSocketResult Failed(string error) => new(false, error);
}

// The seam that keeps Core free of ClientWebSocket and free of the network, exactly as
// INexusTransport does for the HTTP side. Every failure arrives as a value rather than an exception
// so that no caller can mistake "never arrived" for "arrived and said no".
public interface INexusSsoSocket : IDisposable
{
    Task<NexusSsoSocketResult> ConnectAsync(Uri url, CancellationToken cancellationToken);

    Task<NexusSsoSocketResult> SendAsync(string message, CancellationToken cancellationToken);

    // Null means the socket closed, cleanly or otherwise. Either way the session has to reconnect.
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
}

public interface INexusSsoSocketFactory
{
    INexusSsoSocket Create();
}

// Opening a browser is a Windows shell call, so Core only ever sees this.
public interface INexusSsoBrowser
{
    bool TryOpen(string url);
}
