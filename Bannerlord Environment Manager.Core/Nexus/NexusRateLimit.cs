using System.Globalization;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

// The published limit and the spec's limit contradict each other, and both are wrong often enough
// that nothing here hard-codes a number. Every response carries the live figures and those are the
// only figures used. The budget belongs to the user, not to BEM, and is shared with whatever other
// mod manager they run.
public sealed record NexusRateLimit(
    int? HourlyLimit = null,
    int? HourlyRemaining = null,
    DateTimeOffset? HourlyReset = null,
    int? DailyLimit = null,
    int? DailyRemaining = null,
    DateTimeOffset? DailyReset = null)
{
    public static NexusRateLimit Unknown { get; } = new();

    public bool IsExhausted => HourlyRemaining is <= 0 || DailyRemaining is <= 0;

    public bool IsKnown => HourlyRemaining is not null || DailyRemaining is not null;

    public static NexusRateLimit FromHeaders(IReadOnlyDictionary<string, string> headers) => new(
        Number(headers, "X-RL-Hourly-Limit"),
        Number(headers, "X-RL-Hourly-Remaining"),
        Moment(headers, "X-RL-Hourly-Reset"),
        Number(headers, "X-RL-Daily-Limit"),
        Number(headers, "X-RL-Daily-Remaining"),
        Moment(headers, "X-RL-Daily-Reset"));

    public string Describe()
    {
        if (!IsKnown)
            return Strings.Current["Core.Nexus.RateLimit.Unknown"];

        var hourly = HourlyRemaining is { } hour
            ? Strings.Current.Format(
                "Core.Nexus.RateLimit.Hourly.WithLimit",
                hour,
                HourlyLimit?.ToString(CultureInfo.InvariantCulture) ?? Strings.Current["Core.Nexus.RateLimit.UnstatedNumber"])
            : Strings.Current["Core.Nexus.RateLimit.Hourly.Unknown"];

        var daily = DailyRemaining is { } day
            ? Strings.Current.Format(
                "Core.Nexus.RateLimit.Daily.WithLimit",
                day,
                DailyLimit?.ToString(CultureInfo.InvariantCulture) ?? Strings.Current["Core.Nexus.RateLimit.UnstatedNumber"])
            : Strings.Current["Core.Nexus.RateLimit.Daily.Unknown"];

        return Strings.Current.Format("Core.Nexus.RateLimit.Summary", hourly, daily);
    }

    private static int? Number(IReadOnlyDictionary<string, string> headers, string name) =>
        Find(headers, name) is { } text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static DateTimeOffset? Moment(IReadOnlyDictionary<string, string> headers, string name) =>
        Find(headers, name) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;

    private static string? Find(IReadOnlyDictionary<string, string> headers, string name)
    {
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                return header.Value;
        }

        return null;
    }
}
