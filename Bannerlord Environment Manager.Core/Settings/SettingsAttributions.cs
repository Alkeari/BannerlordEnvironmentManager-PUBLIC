using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Settings;

// Who decided the folder belongs to the module. The user saying so is a different kind of claim from
// BEM working it out, and neither may be presented as the other.
public enum AttributionOrigin
{
    Derived,
    Stated
}

public sealed record SettingsAttribution(
    string RelativePath,
    ModuleId ModuleId,
    string ModuleName,
    SettingsMatchSource Source,
    string Evidence,
    DateTimeOffset RecordedUtc,
    AttributionOrigin Origin);

// Evidence inside a settings folder is not permanent: a mod clears its own logs, an update rewrites
// the files that named it, and the folder that BEM attributed last week reads as an unidentified
// mystery today. What was worked out from evidence is kept, with the evidence that established it, so
// a scan that can no longer read it says "remembered" rather than regressing to "no matching module".
public static class SettingsAttributions
{
    // Only evidence that lives inside the folder is worth keeping. A folder that names its module is
    // re-derived from its own name every scan and can never regress, and a guess by resemblance is
    // not a finding to remember.
    public static bool IsWorthRemembering(SettingsFolderReview review)
    {
        ArgumentNullException.ThrowIfNull(review);

        return review.Source is SettingsMatchSource.FileContent or SettingsMatchSource.FileName &&
            !review.MatchedModuleId.IsEmpty;
    }

    public static SettingsAttribution? Derived(SettingsFolderReview review, DateTimeOffset recordedUtc)
    {
        ArgumentNullException.ThrowIfNull(review);

        return IsWorthRemembering(review)
            ? new SettingsAttribution(
                review.Folder.RelativePath,
                review.MatchedModuleId,
                review.MatchedModuleName ?? review.MatchedModuleId.Value,
                review.Source,
                review.Evidence,
                recordedUtc,
                AttributionOrigin.Derived)
            : null;
    }

    public static SettingsAttribution Stated(string relativePath, ModuleId id, string name, DateTimeOffset recordedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        return new SettingsAttribution(
            relativePath,
            id,
            string.IsNullOrWhiteSpace(name) ? id.Value : name,
            SettingsMatchSource.None,
            string.Empty,
            recordedUtc,
            AttributionOrigin.Stated);
    }

    public static IReadOnlyList<SettingsFolderReview> Apply(
        IReadOnlyList<SettingsFolderReview> reviews,
        IReadOnlyList<SettingsAttribution> remembered)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(remembered);

        if (remembered.Count == 0)
            return reviews;

        var byPath = new Dictionary<string, SettingsAttribution>(StringComparer.OrdinalIgnoreCase);

        foreach (var attribution in remembered)
            byPath[attribution.RelativePath] = attribution;

        var applied = new List<SettingsFolderReview>(reviews.Count);

        foreach (var review in reviews)
        {
            if (!byPath.TryGetValue(review.Folder.RelativePath, out var attribution) || !Outranks(attribution, review))
            {
                applied.Add(review);
                continue;
            }

            applied.Add(review with
            {
                Kind = attribution.Origin == AttributionOrigin.Stated
                    ? SettingsMatchKind.Stated
                    : SettingsMatchKind.Remembered,
                MatchedModuleId = attribution.ModuleId,
                MatchedModuleName = attribution.ModuleName,
                Suggestions = [],
                Source = attribution.Source,
                Evidence = attribution.Evidence,
                RememberedUtc = attribution.RecordedUtc
            });
        }

        return applied;
    }

    // What the user states outranks anything BEM inferred, including evidence BEM can read right now.
    // A remembered attribution outranks a guess and outranks nothing at all, and gives way to evidence
    // the folder still carries, because that evidence is the fresher answer to the same question.
    private static bool Outranks(SettingsAttribution attribution, SettingsFolderReview review) =>
        attribution.Origin == AttributionOrigin.Stated ||
        review.Kind is not (SettingsMatchKind.Content or SettingsMatchKind.Exact);
}

public sealed class SettingsAttributionStore(string filePath)
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record Entry(
        string? RelativePath,
        string? ModuleId,
        string? ModuleName,
        SettingsMatchSource Source,
        string? Evidence,
        DateTimeOffset RecordedUtc,
        AttributionOrigin Origin);

    public string FilePath { get; } = filePath;

    // Per version: this file describes one specific load order, so a machine-wide copy showed one
    // version's answer while another was selected. The resting version keeps the path it has always
    // used; every other version reads and writes its own.
    public static string GetDefaultPath(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, "settings-attributions.json");

    // A file that will not parse throws rather than reading as nothing remembered. "There is nothing"
    // and "I could not read it" mean opposite things, and answering the first would let one corrupt
    // file erase every attribution the moment anything wrote the list back.
    public IReadOnlyList<SettingsAttribution> Read()
    {
        if (!File.Exists(FilePath))
            return [];

        var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath), Format) ?? [];

        return
        [
            .. entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.RelativePath) && !string.IsNullOrWhiteSpace(entry.ModuleId))
                .Select(entry => new SettingsAttribution(
                    entry.RelativePath!,
                    new ModuleId(entry.ModuleId!),
                    string.IsNullOrWhiteSpace(entry.ModuleName) ? entry.ModuleId! : entry.ModuleName,
                    entry.Source,
                    entry.Evidence ?? string.Empty,
                    entry.RecordedUtc,
                    entry.Origin))
        ];
    }

    public bool Remember(SettingsAttribution attribution)
    {
        ArgumentNullException.ThrowIfNull(attribution);

        return Remember([attribution]);
    }

    public bool Remember(IEnumerable<SettingsAttribution> attributions)
    {
        ArgumentNullException.ThrowIfNull(attributions);

        var kept = Read().ToList();
        var changed = false;

        foreach (var attribution in attributions)
        {
            var index = kept.FindIndex(item => SamePath(item.RelativePath, attribution.RelativePath));

            if (index < 0)
            {
                kept.Add(attribution);
                changed = true;
                continue;
            }

            if (kept[index].Origin == AttributionOrigin.Stated && attribution.Origin != AttributionOrigin.Stated)
                continue;

            // The same conclusion from the same evidence keeps the date it was first written down, so
            // "remembered from" names when BEM worked it out and not when it last re-read the file.
            if (Restates(kept[index], attribution))
                continue;

            kept[index] = attribution;
            changed = true;
        }

        if (changed)
            Write(kept);

        return changed;
    }

    public bool Forget(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var remembered = Read();
        var kept = remembered.Where(item => !SamePath(item.RelativePath, relativePath)).ToList();

        if (kept.Count == remembered.Count)
            return false;

        Write(kept);

        return true;
    }

    public void Write(IReadOnlyList<SettingsAttribution> attributions)
    {
        ArgumentNullException.ThrowIfNull(attributions);

        var parent = Path.GetDirectoryName(FilePath);

        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        var entries = attributions.Select(item => new Entry(
            item.RelativePath,
            item.ModuleId.Value,
            item.ModuleName,
            item.Source,
            item.Evidence,
            item.RecordedUtc,
            item.Origin));

        File.WriteAllText(FilePath, JsonSerializer.Serialize(entries, Format));
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool Restates(SettingsAttribution current, SettingsAttribution replacement) =>
        current.ModuleId == replacement.ModuleId &&
        current.Source == replacement.Source &&
        current.Origin == replacement.Origin &&
        string.Equals(current.Evidence, replacement.Evidence, StringComparison.Ordinal);
}
