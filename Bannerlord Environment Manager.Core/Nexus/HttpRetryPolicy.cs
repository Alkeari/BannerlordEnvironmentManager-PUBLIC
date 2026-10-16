namespace BannerlordEnvironmentManager.Core.Nexus;

// How a transport failure that is a dropped connection, rather than an answer, is retried. A Nexus
// server or CDN that closes the connection mid-body (the "incomplete chunked read" the real
// transport's HttpClient reports) is the one case a retry helps: the failure is the connection, not
// the request. HTTP status answers (auth, rate limit, not found, a clean body) never reach this
// policy, because retrying a refusal cannot turn it into an answer and only risks spending quota.
public static class HttpRetryPolicy
{
    // One original plus two retries. Enough for a flaky hop, short enough that a genuinely down
    // service is reported without a long wait.
    public const int MaxAttempts = 3;

    // A short, growing delay between attempts: 200ms, 400ms, then 800ms flat. Long enough for the
    // server to settle a reset connection, short enough not to look hung.
    public static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(200 * (1 << Math.Clamp(attempt, 0, 2)));
}
