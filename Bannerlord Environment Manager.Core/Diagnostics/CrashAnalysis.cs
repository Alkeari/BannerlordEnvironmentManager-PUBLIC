using BannerlordEnvironmentManager.Core.DryRun;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Modules.KnownIssues;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// The three static reads a narrowing joins against one crash report, resolved once for a whole folder
// of reports rather than per report. All three are optional: with none of them the analysis behaves
// exactly as it did before the narrowing existed.
public sealed record CrashEvidence(
    HarmonyCallSiteReport? HarmonyCalls = null,
    HarmonyPatchTargetReport? PatchTargets = null,
    IReadOnlyList<GameCrashFolderReading>? CrashFolders = null)
{
    public static CrashEvidence None { get; } = new();

    // Which crash folder this report belongs to. A report written inside a crash folder joins on its
    // own path, which is a fact; otherwise the nearest folder in time is used, which is not, so the
    // window is deliberately tight and no match at all is the normal answer.
    public GameCrashFolderReading? FolderFor(string reportPath, DateTimeOffset written)
    {
        if (CrashFolders is not { Count: > 0 } folders)
            return null;

        var inside = folders.FirstOrDefault(folder =>
            reportPath.StartsWith(
                Path.TrimEndingDirectorySeparator(folder.Path) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));

        if (inside is not null)
            return inside;

        return folders
            .Select(folder => (Folder: folder, Apart: (folder.FaultAt ?? folder.Written) - written))
            .Where(match => match.Apart.Duration() <= JoinWindow)
            .OrderBy(match => match.Apart.Duration())
            .Select(match => match.Folder)
            .FirstOrDefault();
    }

    // Wide enough for a report written while the game was still dying, narrow enough that two
    // different sessions never join. A crash report and the folder for the same crash are minutes
    // apart at the very most.
    public static TimeSpan JoinWindow => TimeSpan.FromMinutes(10);
}

// What the reader needs to know about the registry a verdict was produced against, and whether a
// dry run would improve it. NeedsRefresh is what puts the offer beside the crash that motivated it.
public sealed record PatchRegistryStatus(
    bool HasRegistry,
    bool IsStale,
    bool NeedsRefresh,
    string Message,
    string Details = "");

// The join between a crash report on disk and the patch registry a dry run captured. Without this
// the engine's registry rules are unreachable outside tests: nothing else builds a PatchRegistry.
public static class CrashAnalysis
{
    public static CrashAttribution Analyze(
        CrashReport crash,
        AssemblyIndex index,
        IReadOnlyList<ModuleId> loadedModules,
        PatchRegistryStore? registries,
        string? gameVersion = null,
        int occurrences = 1,
        DateTime? crashTimeUtc = null,
        string? firstChanceRoot = null,
        CrashEvidence? evidence = null,
        string reportPath = "",
        DateTimeOffset written = default)
    {
        ArgumentNullException.ThrowIfNull(crash);

        return Analyze(
            crash, index, loadedModules, Look(registries, loadedModules, gameVersion), gameVersion, occurrences,
            crashTimeUtc, firstChanceRoot, evidence, reportPath, written);
    }

    // A folder of crash reports shares one registry, so the lookup is resolved once and handed in
    // rather than re-read and re-parsed per report.
    public static CrashAttribution Analyze(
        CrashReport crash,
        AssemblyIndex index,
        IReadOnlyList<ModuleId> loadedModules,
        PatchRegistryLookup lookup,
        string? gameVersion = null,
        int occurrences = 1,
        DateTime? crashTimeUtc = null,
        string? firstChanceRoot = null,
        CrashEvidence? evidence = null,
        string reportPath = "",
        DateTimeOffset written = default)
    {
        ArgumentNullException.ThrowIfNull(crash);
        ArgumentNullException.ThrowIfNull(lookup);

        var found = evidence ?? CrashEvidence.None;

        return Attribution.Analyze(new AttributionInput(
            crash, index, loadedModules, lookup.Registry, gameVersion, occurrences,
            FirstChanceTrace.ReadNewest(firstChanceRoot ?? FirstChancePaths.GetDefaultRoot()),
            crashTimeUtc,
            EnhancedStacktrace.Parse(crash.RawText),
            found.HarmonyCalls,
            found.PatchTargets,
            found.FolderFor(reportPath, written)?.Boundary));
    }

    public static PatchCollisionReport Collisions(
        PatchRegistryStore? registries,
        IReadOnlyList<ModuleId> loadedModules,
        string? gameVersion = null) =>
        PatchCollisions.Build(Look(registries, loadedModules, gameVersion).Registry);

    public static PatchRegistryLookup Look(
        PatchRegistryStore? registries,
        IReadOnlyList<ModuleId> loadedModules,
        string? gameVersion = null) =>
        registries is null
            ? new PatchRegistryLookup(null, false, Strings.Current["Core.Diagnostics.CrashAnalysis.NoRegistryStore"])
            : registries.Load(loadedModules ?? [], gameVersion);

    public static PatchRegistryStatus Describe(PatchRegistryLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        if (!lookup.CanAttribute)
        {
            return new PatchRegistryStatus(
                false,
                false,
                true,
                lookup.Reason + Strings.Current["Core.Diagnostics.CrashAnalysis.SweepSuffix"]);
        }

        return new PatchRegistryStatus(true, lookup.IsStale, lookup.IsStale, lookup.Reason, lookup.Details);
    }
}
