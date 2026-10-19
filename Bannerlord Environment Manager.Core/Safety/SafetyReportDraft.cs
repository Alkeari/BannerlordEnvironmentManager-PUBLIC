using System.Globalization;
using System.Text;
using System.Text.Json;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Safety;

// What BEM would contribute to the community blocklist about one mod, built here so the user can read
// every character of it before any of it leaves the machine.
//
// BEM never submits this and has no credential that could. The draft is shown, copied by the user and
// posted by the user, which is the only way anything here reaches anyone else.
public sealed record SafetyReportDraft(
    string Json,
    string Evidence,
    string? WorkshopId,
    IReadOnlyList<string> Sha256)
{
    // A name is not an identity. Two authors can ship a mod with the same title, and a report that
    // says only "Better Battles is malicious" convicts both of them. Without a Workshop id or a file
    // hash there is nothing to report, and BEM says so rather than reporting the name on its own.
    public bool CanIdentify => WorkshopId is not null || Sha256.Count > 0;

    public string IdentityText => CanIdentify
        ? WorkshopId is null
            ? Strings.Current.Plural("Core.Safety.Report.IdentifiedByHashes", Sha256.Count)
            : Strings.Current.Format("Core.Safety.Report.IdentifiedByWorkshopId", WorkshopId)
        : SafetyReport.NameIsNotAnIdentity;
}

public static class SafetyReport
{
    // The community repository this blocklist comes from. Opened in the user's browser on their press;
    // BEM never posts to it.
    public const string IssuesUrl = "https://github.com/mazetankzz-gif/calradia-warden/issues/new";

    public static string WhyNothingIsSent =>
        Strings.Current["Core.Safety.Report.WhyNothingIsSent"];

    public static string NameIsNotAnIdentity =>
        Strings.Current["Core.Safety.Report.NameIsNotAnIdentity"];

    public static string WhyNameIsNotEnough =>
        Strings.Current["Core.Safety.Report.WhyNameIsNotEnough"];

    public static SafetyReportDraft For(SafetyScanResult result, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(result);

        var hashes = Hashes(result);

        return new SafetyReportDraft(
            Payload(result, hashes, today),
            Evidence(result),
            WorkshopIdOf(result),
            hashes);
    }

    // Whether this result may become a public accusation against a named author, and the sentence to
    // say when it may not. A false report is the expensive failure here and a missed one is not, so
    // the gate refuses by default and only a scan that both found something and can name the exact mod
    // gets through. Reading it costs no strings: it never builds the payload.
    public static string? WhyNotReportable(SafetyScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.NothingCouldBeRead)
            return Strings.Current["Core.Safety.Report.NothingToReport.Unreadable"];

        if (!result.IsAlarm)
            return Strings.Current["Core.Safety.Report.NothingToReport.Clean"];

        return WorkshopIdOf(result) is null && Hashes(result).Count == 0
            ? $"{NameIsNotAnIdentity} {WhyNameIsNotEnough}"
            : null;
    }

    private static string? WorkshopIdOf(SafetyScanResult result) =>
        result.Target.WorkshopId is { Length: > 0 } id ? id : null;

    private static List<string> Hashes(SafetyScanResult result) =>
    [
        .. result.Flagged
            .Select(file => file.Sha256)
            .OfType<string>()
            .Select(hash => hash.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
    ];

    // Written field by field rather than serialized off an object, so the payload on screen is the
    // payload in the order it is written and there is no chance of a member appearing that the user
    // was not shown.
    private static string Payload(SafetyScanResult result, IReadOnlyList<string> hashes, DateOnly today)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("name", result.Target.Name);

            if (result.Target.WorkshopId is { Length: > 0 } workshopId)
                writer.WriteString("workshopId", workshopId);

            if (hashes.Count > 0)
            {
                writer.WriteStartArray("sha256");

                foreach (var hash in hashes)
                    writer.WriteStringValue(hash);

                writer.WriteEndArray();
            }

            writer.WriteString("reason", Reason(result));
            writer.WriteString("date", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Reason(SafetyScanResult result)
    {
        var signals = result.Flagged
            .SelectMany(file => file.Evidence)
            .Where(evidence => evidence.Signal is not SafetySignal.BlocklistWorkshopId
                and not SafetySignal.BlocklistFileHash)
            .Select(evidence => evidence.Headline)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return signals.Count == 0
            ? "Reported by a BEM user."
            : string.Join("; ", signals) + ".";
    }

    // Names files by their file name and archive entries by their entry path, never by where they sit
    // on this machine. A report is posted in public, and an absolute path carries the user's account
    // name into it for no benefit at all.
    private static string Evidence(SafetyScanResult result)
    {
        var text = new StringBuilder();

        text.AppendLine($"Found by Bannerlord Environment Manager, {result.VerdictText}.");
        text.AppendLine(result.VerdictExplanation);
        text.AppendLine();

        foreach (var file in result.Flagged)
        {
            text.AppendLine(Where(file));

            foreach (var evidence in file.Evidence)
                text.AppendLine($"  {evidence.Headline}: {evidence.Matched}");

            if (file.Sha256 is { } hash)
                text.AppendLine($"  sha256 {hash}");

            text.AppendLine();
        }

        if (result.Capabilities.Count > 0)
            text.AppendLine($"Capabilities read off its metadata: {string.Join(", ", result.Capabilities)}.");

        if (result.UnrecognizedUrls.Count > 0)
            text.AppendLine($"Hosts it names: {string.Join(", ", result.UnrecognizedUrls)}.");

        return text.ToString().TrimEnd();
    }

    private static string Where(FlaggedFile file) =>
        file.EntryPath ?? Path.GetFileName(file.FilePath);
}
