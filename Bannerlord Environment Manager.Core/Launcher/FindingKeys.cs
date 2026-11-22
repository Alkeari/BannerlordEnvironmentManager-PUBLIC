using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Modules.KnownIssues;

namespace BannerlordEnvironmentManager.Core.Launcher;

// One identity for a finding wherever it is shown, so accepting it on the Health pages hides it from the
// launch check and the other way round. The scoped key is the same FindingIgnoreKey the launch check
// assigns (class, module, module version, subject, game line); the subjects are built here so a Health
// surface and the launch check cannot disagree about them.
//
// The Legacy members reproduce the (Category, Heading) keys the Health pages stored before scoped keys
// existed, from the same catalog entries those pages format, so an acceptance already on record there
// keeps hiding the launch check's copy of the finding.
public static class FindingKeys
{
    // A module's unresolved game API references are one finding per module on every surface.
    public const string WholeModule = "module";

    public static string Scoped(
        PreflightCheck check, ModuleId? moduleId, string? moduleVersion, string subject, ModuleVersion gameVersion) =>
        FindingIgnoreKey.For(check.ToString(), moduleId, moduleVersion, subject, gameVersion).ToString();

    public static string ModuleVersionOf(ModuleEntry? entry) =>
        entry?.Manifest?.VersionText is { Length: > 0 } text ? text : entry?.Version.ToString() ?? string.Empty;

    public static string HarmonySubject(HarmonyPatchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return $"{target.PatchClass}->{target.Target}";
    }

    public static string VersionGapSubject(VersionedAssemblySet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        return set.Stem;
    }

    public static string DuplicateIdSubject(ModuleId id) => $"{IssueKind.DuplicateModuleId}:{id.Value}";

    public static string ShadowedAssemblySubject(string fileName) => fileName;

    public static string LegacyHarmony(HarmonyPatchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return AcceptedFindingStore.KeyFor(
            "HarmonyPatchIssue", Strings.Current.Format("Diagnostics.Harmony.Heading", target.ModuleName, target.Target));
    }

    // The Health heading carries a count of references that page counted (all builds of a family), which
    // the launch check does not reproduce, so any count matches.
    public static Regex LegacyMissingApi(string moduleName)
    {
        const int Sentinel = 987654321;

        var forms = new[] { 1, Sentinel }
            .Select(count => Strings.Current.Plural("Diagnostics.MissingApi.Heading", count, moduleName))
            .Distinct(StringComparer.Ordinal)
            .Select(heading => Regex.Escape(AcceptedFindingStore.KeyFor("MissingApiIssue", heading))
                .Replace(Sentinel.ToString(System.Globalization.CultureInfo.InvariantCulture), @"\d+"));

        return new Regex("^(?:" + string.Join("|", forms) + ")$", RegexOptions.CultureInvariant);
    }

    // Written with the grade in front ("Note: ..." before every gap became a Warning), so both forms match.
    public static IReadOnlyList<string> LegacyVersionGap(GameVersionGap gap)
    {
        ArgumentNullException.ThrowIfNull(gap);

        return [AcceptedFindingStore.KeyFor("VersionGap", $"Warning: {gap.Headline}"),
            AcceptedFindingStore.KeyFor("VersionGap", $"Note: {gap.Headline}")];
    }

    public static string LegacyDuplicateId(ModuleId id) =>
        AcceptedFindingStore.KeyFor("DuplicateId", Strings.Current.Format("Diagnostics.DuplicateId.Heading", id));

    public static string LegacyShadowedAssembly(ShadowedAssembly shadowed)
    {
        ArgumentNullException.ThrowIfNull(shadowed);

        return AcceptedFindingStore.KeyFor("AssemblyIssue", shadowed.Summarize());
    }

    public static string LegacySubModuleClash(DuplicateSubModuleClass clash)
    {
        ArgumentNullException.ThrowIfNull(clash);

        return AcceptedFindingStore.KeyFor("SubModuleClash", clash.Summarize());
    }
}
