using System.Text.Json;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Butr;

public enum ButrScoreStatus
{
    Disabled,
    NothingToAsk,
    Fetched,
    Unreachable,
    ServiceError,
    Canceled
}

public sealed record ButrRequest(
    IReadOnlyList<ButrModuleRequest> Modules,
    string? GameVersion,
    bool Enabled,
    TimeSpan CacheLifetime,
    DateTimeOffset Now,
    bool ForceRefresh = false);

public sealed record ButrReport(
    ButrScoreStatus Status,
    string Message,
    IReadOnlyList<ButrModuleScore> Scores,
    IReadOnlyList<string> ModulesWithoutData,
    DateTimeOffset? FetchedUtc,
    bool FromCache);

public sealed record ButrOptions(bool Enabled = true, int CacheHours = 2)
{
    public static ButrOptions Default { get; } = new();

    public TimeSpan CacheLifetime => TimeSpan.FromHours(Math.Clamp(CacheHours, 1, 168));
}

public sealed class ButrOptionsStore(string filePath)
{
    public const string FileName = "butr-settings.json";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string FilePath { get; } = filePath;

    public ButrOptions Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<ButrOptions>(File.ReadAllText(FilePath), Format) ?? ButrOptions.Default
                : ButrOptions.Default;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return ButrOptions.Default;
        }
    }

    public void Save(ButrOptions options)
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

public sealed record ButrCachedScores(
    DateTimeOffset FetchedUtc,
    string GameVersion,
    string ModuleFingerprint,
    IReadOnlyList<ButrModuleScore> Scores)
{
    public bool IsFreshAt(DateTimeOffset now, TimeSpan lifetime) =>
        now >= FetchedUtc && now - FetchedUtc < lifetime;
}

public sealed class ButrScoreCache(string filePath)
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string FilePath { get; } = filePath;

    public ButrCachedScores? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<ButrCachedScores>(File.ReadAllText(FilePath), Format)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(ButrCachedScores scores)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(scores, Format));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}

// Advice, and only advice. A score never changes a load order, never changes a sort and never
// changes the severity of anything BEM validates: nothing in this namespace knows those types exist.
//
// A module the service has no crash data for is absent from its answer. That absence is reported as
// an absence, because reading it as a clean bill of health would be the worst thing this feature
// could say.
public sealed class ButrCompatibility(ButrClient client, ButrScoreCache cache)
{
    public async Task<ButrReport> RunAsync(ButrRequest request, CancellationToken cancellationToken)
    {
        if (!request.Enabled)
            return Nothing(ButrScoreStatus.Disabled, Strings.Current["Core.Butr.Compatibility.Disabled"]);

        if (request.Modules.Count == 0)
            return Nothing(ButrScoreStatus.NothingToAsk, Strings.Current["Core.Butr.Compatibility.NothingToAsk"]);

        if (ButrGameVersion.Format(request.GameVersion) is not { } gameVersion)
            return Nothing(ButrScoreStatus.NothingToAsk, Strings.Current["Core.Butr.Compatibility.NoGameVersion"]);

        var fingerprint = Fingerprint(request.Modules);
        var cached = cache.Load();

        if (!request.ForceRefresh
            && cached is not null
            && cached.GameVersion == gameVersion
            && cached.ModuleFingerprint == fingerprint
            && cached.IsFreshAt(request.Now, request.CacheLifetime))
            return Describe(cached, request.Modules, fromCache: true);

        var result = await client.GetScoresAsync(gameVersion, request.Modules, cancellationToken);

        if (result is { Outcome: ButrOutcome.Ok, Scores: { } scores })
        {
            var fresh = new ButrCachedScores(request.Now, gameVersion, fingerprint, scores);
            cache.Save(fresh);

            return Describe(fresh, request.Modules, fromCache: false);
        }

        var status = result.Outcome switch
        {
            ButrOutcome.Unreachable => ButrScoreStatus.Unreachable,
            ButrOutcome.Canceled => ButrScoreStatus.Canceled,
            _ => ButrScoreStatus.ServiceError
        };

        var fallback = status == ButrScoreStatus.Canceled ? null : cached;

        return new ButrReport(
            status,
            fallback is null
                ? result.Message
                : Strings.Current.Format("Core.Butr.Compatibility.StaleFallback", result.Message, fallback.FetchedUtc.ToString("yyyy-MM-dd HH:mm")),
            fallback?.Scores ?? [],
            fallback is null ? [] : WithoutData(fallback.Scores, request.Modules),
            fallback?.FetchedUtc,
            fallback is not null);
    }

    private static ButrReport Describe(ButrCachedScores cached, IReadOnlyList<ButrModuleRequest> modules, bool fromCache)
    {
        var missing = WithoutData(cached.Scores, modules);

        var message = cached.Scores.Count == 0
            ? Strings.Current.Plural("Core.Butr.Compatibility.NoDataForAny", modules.Count)
            : Strings.Current.Plural("Core.Butr.Compatibility.ScoresReturned", modules.Count, cached.Scores.Count);

        if (missing.Count > 0 && cached.Scores.Count > 0)
            message += $" {Strings.Current.Plural("Core.Butr.Compatibility.OthersNoData", missing.Count)}";

        return new ButrReport(ButrScoreStatus.Fetched, message, cached.Scores, missing, cached.FetchedUtc, fromCache);
    }

    private static IReadOnlyList<string> WithoutData(
        IReadOnlyList<ButrModuleScore> scores,
        IReadOnlyList<ButrModuleRequest> modules)
    {
        var scored = scores.Select(score => score.ModuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. modules.Where(module => !scored.Contains(module.ModuleId)).Select(module => module.ModuleId)];
    }

    // The answer depends on exactly which modules were asked about, so a changed list is never
    // answered from the previous one.
    private static string Fingerprint(IReadOnlyList<ButrModuleRequest> modules) =>
        string.Join('|', modules
            .Select(module => $"{module.ModuleId}@{module.ModuleVersion}")
            .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase));

    private static ButrReport Nothing(ButrScoreStatus status, string message) =>
        new(status, message, [], [], null, false);
}
