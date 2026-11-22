using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// What an "ignore always" is scoped to: the class of finding, the module it is about, that module's
// version, the canonical untranslated subject (an API, a patch target, a file and line), and the game's
// major.minor line. A module update or a move to a new game line brings an ignored finding back once,
// and a game patch inside the same line does not. The older (Check, Headline) key carried neither
// version, so an update could leave a stale acceptance, and a reworded headline silently dropped one.
public sealed record FindingIgnoreKey(
    string ClassId,
    string ModuleId,
    string ModuleVersion,
    string Subject,
    string GameLine)
{
    // Stored keys carry this tag so a scoped key is never mistaken for a legacy "Check|Headline" one,
    // whose headline may itself contain the separator.
    public const string Tag = "scoped:";

    private const char Separator = '|';

    public static FindingIgnoreKey For(
        string classId, ModuleId? moduleId, string? moduleVersion, string subject, ModuleVersion gameVersion) =>
        new(
            Clean(classId),
            Clean(moduleId?.Value),
            Clean(moduleVersion),
            Clean(subject),
            Clean(Diagnostics.GameLine.Of(gameVersion)));

    // The same finding, whatever the module version or game line it was ignored under.
    public bool SameFindingAs(FindingIgnoreKey other) =>
        other is not null
        && string.Equals(ClassId, other.ClassId, StringComparison.Ordinal)
        && string.Equals(ModuleId, other.ModuleId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Subject, other.Subject, StringComparison.Ordinal);

    public bool ModuleVersionDiffersFrom(FindingIgnoreKey other) =>
        !string.Equals(ModuleVersion, other.ModuleVersion, StringComparison.OrdinalIgnoreCase);

    public bool GameLineDiffersFrom(FindingIgnoreKey other) =>
        !string.Equals(GameLine, other.GameLine, StringComparison.Ordinal);

    // Why a finding ignored before is shown again: the module updated, the game moved to another minor
    // line, or both. Empty when nothing in the scope changed, which is never a reason to show it.
    public static string DescribeChange(FindingIgnoreKey prior, FindingIgnoreKey now)
    {
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(now);

        var parts = new List<string>();

        if (now.ModuleVersionDiffersFrom(prior))
            parts.Add(Localization.Strings.Current.Format("Core.Diagnostics.IgnoreKey.Changed.ModuleVersion",
                now.ModuleId, prior.ModuleVersion, now.ModuleVersion));

        if (now.GameLineDiffersFrom(prior))
            parts.Add(Localization.Strings.Current.Format("Core.Diagnostics.IgnoreKey.Changed.GameLine",
                prior.GameLine, now.GameLine));

        return string.Join(" ", parts);
    }

    // ClassId|ModuleId|ModuleVersion|Subject|GameLine, behind the tag.
    public override string ToString() =>
        Tag + string.Join(Separator, ClassId, ModuleId, ModuleVersion, Subject, GameLine);

    public static FindingIgnoreKey? Parse(string? text)
    {
        if (text is null || !text.StartsWith(Tag, StringComparison.Ordinal))
            return null;

        var parts = text[Tag.Length..].Split(Separator);

        return parts.Length == 5 ? new FindingIgnoreKey(parts[0], parts[1], parts[2], parts[3], parts[4]) : null;
    }

    public static bool IsScoped(string? text) => Parse(text) is not null;

    // The separator can never appear inside a part, so a key always splits back into exactly five.
    private static string Clean(string? part) =>
        (part ?? string.Empty).Replace(Separator, '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
