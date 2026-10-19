using System.Text.Json;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Butr;

public sealed record ButrUploadResponse(int StatusCode, string Body, string? TransportError = null)
{
    public static ButrUploadResponse Failed(string error) => new(0, string.Empty, error);

    public bool IsSuccess => TransportError is null && StatusCode is 200 or 201;
}

// Separate from IButrTransport because this is a different host, a different verb's worth of headers
// and a gzip body. Keeps Core free of HttpClient either way.
public interface IButrCrashTransport
{
    Task<ButrUploadResponse> UploadAsync(string json, CancellationToken cancellationToken);
}

public enum ButrContributionStatus
{
    Disabled,
    NothingToSend,
    AlreadySent,
    DailyLimitReached,
    Sent,
    Unreachable,
    ServiceError,
    Canceled
}

public sealed record ButrContributionResult(ButrContributionStatus Status, string Message, string? ReportUrl = null)
{
    public bool WasSent => Status is ButrContributionStatus.Sent;
}

// The one thing in BEM that is off until the user turns it on. Everything else ships on, because
// everything else changes only BEM's own state. This sends the user's data to a third party and a
// publish cannot be taken back, which is exactly the carve-out the project's rule names.
public sealed record ButrContributionOptions(bool Enabled = false, int MaxPerDay = 10)
{
    public static ButrContributionOptions Default { get; } = new();

    public int DailyLimit => Math.Clamp(MaxPerDay, 1, 50);
}

public sealed class ButrContributionOptionsStore(string filePath)
{
    public const string FileName = "butr-contribution.json";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string FilePath { get; } = filePath;

    public ButrContributionOptions Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<ButrContributionOptions>(File.ReadAllText(FilePath), Format)
                  ?? ButrContributionOptions.Default
                : ButrContributionOptions.Default;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return ButrContributionOptions.Default;
        }
    }

    public void Save(ButrContributionOptions options)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(options, Format));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}

public sealed record ButrSentCrash(string Fingerprint, DateTimeOffset SentUtc, string? ReportUrl);

// What has already gone, so the same crash is never counted twice. The fingerprint is local only:
// nothing derived from it is ever sent, because two people hitting the same crash are two data
// points and a shared identifier would collapse them into one.
public sealed class ButrSubmissionLedger(string filePath)
{
    public const string FileName = "butr-sent-crashes.json";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string FilePath { get; } = filePath;

    public IReadOnlyList<ButrSentCrash> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<ButrSentCrash>>(File.ReadAllText(FilePath), Format) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    public void Record(ButrSentCrash sent)
    {
        try
        {
            var all = Load().Append(sent).TakeLast(500).ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, Format));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}

// Gives back to the dataset BEM reads from. The only thing BUTR accepts is a crash report: there is
// no endpoint for a run that went well, so a clean launch has nowhere to go and is not invented.
//
// Nothing waits on this. It cancels, it is capped, it never throws, and its failure is invisible
// except on the screen where the user asked for it.
public sealed class ButrContribution(IButrCrashTransport transport, ButrSubmissionLedger ledger)
{
    public static string UnreachableMessage => Strings.Current["Core.Butr.Contribution.Unreachable"];

    // Built without sending, so the user reads the exact bytes before deciding. Nothing here
    // touches the network or the ledger.
    public ButrPayload Prepare(ButrCrashSubmission submission, ButrContributionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Enabled
            ? ButrCrashPayload.Build(submission, Guid.NewGuid())
            : new ButrPayload(
                ButrPayloadOutcome.NotAttributed,
                string.Empty,
                string.Empty,
                Strings.Current["Core.Butr.Contribution.PrepareDisabled"]);
    }

    public async Task<ButrContributionResult> SendAsync(
        ButrPayload payload,
        ButrContributionOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
            return new ButrContributionResult(ButrContributionStatus.Disabled, Strings.Current["Core.Butr.Contribution.SendDisabled"]);

        if (!payload.IsReady)
            return new ButrContributionResult(ButrContributionStatus.NothingToSend, payload.Reason);

        var sent = ledger.Load();

        if (sent.Any(entry => entry.Fingerprint == payload.Fingerprint))
            return new ButrContributionResult(ButrContributionStatus.AlreadySent, Strings.Current["Core.Butr.Contribution.AlreadySent"]);

        if (sent.Count(entry => entry.SentUtc > now - TimeSpan.FromDays(1)) >= options.DailyLimit)
            return new ButrContributionResult(ButrContributionStatus.DailyLimitReached,
                Strings.Current.Plural("Core.Butr.Contribution.DailyLimitReached", options.DailyLimit));

        ButrUploadResponse response;

        try
        {
            response = await transport.UploadAsync(payload.Json, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new ButrContributionResult(ButrContributionStatus.Canceled, Strings.Current["Core.Butr.Contribution.Canceled"]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ButrContributionResult(ButrContributionStatus.Unreachable, $"{UnreachableMessage} {ex.Message}");
        }

        if (response.TransportError is { } error)
            return new ButrContributionResult(ButrContributionStatus.Unreachable, $"{UnreachableMessage} {error}");

        if (!response.IsSuccess)
            return new ButrContributionResult(ButrContributionStatus.ServiceError,
                Strings.Current.Format("Core.Butr.Contribution.ServiceError", response.StatusCode));

        var url = FirstLine(response.Body);

        ledger.Record(new ButrSentCrash(payload.Fingerprint, now, url));

        return new ButrContributionResult(
            ButrContributionStatus.Sent,
            url is null
                ? Strings.Current["Core.Butr.Contribution.SentNoUrl"]
                : Strings.Current.Format("Core.Butr.Contribution.SentWithUrl", url),
            url);
    }

    private static string? FirstLine(string body)
    {
        var line = body.ReplaceLineEndings("\n").Split('\n').FirstOrDefault()?.Trim();

        return string.IsNullOrWhiteSpace(line) ? null : line;
    }
}
