using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public enum MissingMemberKind
{
    TypeMissing,
    MemberMissing,
    SignatureChanged
}

public sealed record RetargetCandidate(
    string TargetSignature,
    string TargetAssemblyName,
    string BackupPath,
    Action Apply)
{
    public IReadOnlySet<string> RemovedTypeReferences { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

public sealed record MissingMemberFinding(
    ModuleId ModuleId,
    string ModuleName,
    string ModuleVersion,
    string AssemblyFileName,
    string AssemblyPath,
    MissingMemberKind Kind,
    string DeclaringType,
    string MemberName,
    string ReferencedSignature,
    IReadOnlyList<string> AvailableSignatures,
    IReadOnlyList<string> Callers,
    RetargetCandidate? Retarget = null)
{
    public string Member => string.IsNullOrEmpty(MemberName) ? DeclaringType : $"{DeclaringType}.{MemberName}";

    public bool IsRetargetable => Retarget is not null;
}

// Resolves every member and type reference of each enabled community module's loaded assemblies against
// the instance's game and module assemblies with System.Reflection.Metadata, and offers an in-place
// binary retargeting fix when the installed game has a compatible overload (as Death for All v7.0.0's
// call to Helpers.PerkHelper.AddPerkBonusForParty did on Bannerlord v1.4.8 + War Sails).
public static class MissingMembers
{
    public const string DefaultPlatformFolder = "Win64_Shipping_Client";

    public static IReadOnlyList<MissingMemberFinding> Inspect(
        string gameInstallPath,
        IReadOnlyList<ModuleEntry> entries,
        string? loadedPlatformFolder = null,
        ModuleVersion gameVersion = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameInstallPath);
        ArgumentNullException.ThrowIfNull(entries);

        return Inspect(AssemblyIndex.Build(gameInstallPath), entries, loadedPlatformFolder, gameVersion);
    }

    public static IReadOnlyList<MissingMemberFinding> Inspect(
        AssemblyIndex index,
        IReadOnlyList<ModuleEntry> entries,
        string? loadedPlatformFolder = null,
        ModuleVersion gameVersion = default,
        MissingApiReport? precomputedApiReport = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(entries);

        if (index.Failed)
            return [];

        var platform = string.IsNullOrWhiteSpace(loadedPlatformFolder) ? DefaultPlatformFolder : loadedPlatformFolder;
        var enabledById = entries
            .Where(e => e.IsEnabled && !e.IsOrphan)
            .GroupBy(e => e.Id)
            .ToDictionary(g => g.Key, g => g.First());

        // The preflight has already scanned the same assemblies for the same game; scanning them again was
        // a third of its time.
        var apiReport = precomputedApiReport ?? MissingGameApis.Inspect(index, entries, platform, gameVersion);
        if (apiReport.Findings.Count == 0)
            return [];

        var activeAssemblies = SelectActiveAssemblies(index.Assemblies, enabledById, platform, gameVersion);
        var findings = new List<MissingMemberFinding>();

        foreach (var group in apiReport.Findings.GroupBy(f => (f.ModuleId, f.AssemblyPath)))
        {
            if (!enabledById.TryGetValue(group.Key.ModuleId, out var moduleEntry) || moduleEntry.IsOfficial)
                continue;

            var moduleVersion = moduleEntry.Manifest?.Version.ToString() ?? string.Empty;
            var assemblyFileName = Path.GetFileName(group.Key.AssemblyPath);
            var rawItems = group.ToList();

            var retargetMap = BuildAssemblyRetargetMap(
                group.Key.AssemblyPath,
                moduleVersion,
                rawItems,
                activeAssemblies);

            foreach (var raw in rawItems)
            {
                // Only suppress a type reference proved absent from the proposed replacement. An empty
                // caller list also describes field and base-type declarations, which still need warning.
                if (raw.Kind == MissingApiKind.TypeMissing
                    && retargetMap.Values.Any(candidate => candidate.RemovedTypeReferences.Contains(raw.TypeName)))
                    continue;

                var kind = raw.Kind switch
                {
                    MissingApiKind.TypeMissing => MissingMemberKind.TypeMissing,
                    MissingApiKind.SignatureChanged => MissingMemberKind.SignatureChanged,
                    _ => MissingMemberKind.MemberMissing
                };

                var memberName = raw.MemberName ?? string.Empty;
                var retarget = memberName.Length > 0
                    ? retargetMap.GetValueOrDefault((raw.TypeName, memberName))
                    : null;

                findings.Add(new MissingMemberFinding(
                    raw.ModuleId,
                    raw.ModuleName,
                    moduleVersion,
                    assemblyFileName,
                    raw.AssemblyPath,
                    kind,
                    raw.TypeName,
                    memberName,
                    raw.Signature ?? raw.Api,
                    raw.Available,
                    raw.UsedBy,
                    retarget));
            }
        }

        return
        [
            .. findings
                .OrderByDescending(f => f.IsRetargetable)
                .ThenBy(f => f.Kind == MissingMemberKind.TypeMissing ? 1 : 0)
                .ThenBy(f => f.ModuleName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Member, StringComparer.Ordinal)
        ];
    }

    public static void AddToPreflight(
        PreflightRequest request, List<PreflightFinding> findings, MissingApiReport? precomputedApiReport = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(findings);

        if (request.Assemblies is not { Failed: false } index)
            return;

        var report = Inspect(
            index, request.Environment.Entries, request.LoadedPlatformFolder, request.GameVersion, precomputedApiReport);
        if (report.Count == 0)
            return;

        var otherLinesPresent = request.OtherGameBuilds is { Count: > 0 };

        foreach (var group in report.GroupBy(f => f.ModuleId))
        {
            var items = group.ToList();
            var primary = items.FirstOrDefault(i => i.IsRetargetable)
                ?? items.FirstOrDefault(i => i.Kind != MissingMemberKind.TypeMissing)
                ?? items[0];
            var additional = items.Where(item => primary.IsRetargetable
                    ? item.Retarget is null : !ReferenceEquals(item, primary))
                .Select(item => item.ReferencedSignature).Distinct(StringComparer.Ordinal)
                .OrderBy(signature => signature, StringComparer.Ordinal).ToList();
            var additionalDetail = additional.Count == 0 ? string.Empty : "\n\n" + Strings.Current.Format(
                primary.IsRetargetable ? "Core.Modules.MissingMember.Remaining" : "Core.Modules.MissingMember.Additional",
                string.Join("\n", additional));

            // When another game line on disk already explains a non-retargetable module via BuiltForLine,
            // leave BuiltForLine's row in place rather than duplicating it.
            if (!primary.IsRetargetable
                && otherLinesPresent
                && findings.Any(f => f.Check == PreflightCheck.BuiltForLine && f.ModuleId == group.Key))
            {
                continue;
            }

            // Replace any generic BuiltForLine ("Turn off X") or hidden MissingGameApi row for this module
            // with the specific member finding (and its retargeting fix when one exists).
            // What a crash log would have to name stays with the row that takes their place (LaunchEvidence).
            IReadOnlyList<string> faults =
            [
                .. findings
                    .Where(f => f.ModuleId == group.Key && f.Check is PreflightCheck.MissingGameApi or PreflightCheck.BuiltForLine)
                    .SelectMany(f => f.Faults)
                    .Distinct(StringComparer.Ordinal)
            ];

            findings.RemoveAll(f =>
                f.ModuleId == group.Key
                && f.Check is PreflightCheck.MissingGameApi or PreflightCheck.BuiltForLine);

            if (primary.Retarget is { } retarget)
            {
                var applyAll = () =>
                {
                    foreach (var apply in items.Where(i => i.Retarget is not null).Select(i => i.Retarget!.Apply).Distinct())
                        apply();
                };

                findings.Add(new PreflightFinding(
                    PreflightGrade.Warning,
                    PreflightCheck.MissingGameApi,
                    Strings.Current.Format("Core.Modules.MissingMember.Headline", primary.ModuleName, primary.Member),
                    Strings.Current.Format(
                        "Core.Modules.MissingMember.Retarget.Detail",
                        primary.AssemblyFileName,
                        primary.ReferencedSignature,
                        retarget.TargetSignature) + additionalDetail,
                    primary.ModuleId,
                    Fix: environment =>
                    {
                        applyAll();
                        return environment;
                    },
                    FixLabel: Strings.Current.Format("Core.Modules.MissingMember.Retarget.FixLabel", primary.ModuleName),
                    Remedy: applyAll,
                    Subject: primary.Member,
                    ClassId: "MissingMember")
                {
                    AlternatePattern = FindingKeys.LegacyMissingApi(primary.ModuleName),
                    Faults = faults
                });
            }
            else
            {
                findings.Add(new PreflightFinding(
                    PreflightGrade.Warning,
                    PreflightCheck.MissingGameApi,
                    Strings.Current.Format("Core.Modules.MissingMember.Headline", primary.ModuleName, primary.Member),
                    Strings.Current.Format(
                        "Core.Modules.MissingMember.Detail",
                        primary.AssemblyFileName,
                        primary.ReferencedSignature) + additionalDetail,
                    primary.ModuleId,
                    Subject: primary.Member,
                    ClassId: "MissingMember")
                {
                    AlternatePattern = FindingKeys.LegacyMissingApi(primary.ModuleName),
                    Faults = faults
                });
            }
        }
    }

    private static List<IndexedAssembly> SelectActiveAssemblies(
        IReadOnlyList<IndexedAssembly> all,
        Dictionary<ModuleId, ModuleEntry> enabledById,
        string platform,
        ModuleVersion gameVersion)
    {
        var loadable = all
            .Where(a => a.Origin == AssemblyOrigin.Game || (a.ModuleId is { } id && enabledById.ContainsKey(id)))
            .ToList();

        var moduleAssemblies = loadable
            .Where(a => a.Origin == AssemblyOrigin.Module && InLoadedPlatformFolder(a.Path, platform))
            .ToList();

        var activeModulePaths = LoadedForGame(moduleAssemblies, gameVersion)
            .Select(a => a.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. loadable.Where(a =>
                a.Origin == AssemblyOrigin.Game
                || !InLoadedPlatformFolder(a.Path, platform)
                || activeModulePaths.Contains(a.Path))
        ];
    }

    private static List<IndexedAssembly> LoadedForGame(List<IndexedAssembly> scanned, ModuleVersion gameVersion)
    {
        if (gameVersion.IsEmpty)
            return scanned;

        var kept = new List<IndexedAssembly>();

        var families = scanned
            .Select(a => (Assembly: a, Split: GameVersionSupport.Split(Path.GetFileName(a.Path))))
            .GroupBy(x => x.Split is { } split
                ? Path.GetDirectoryName(x.Assembly.Path) + "|" + split.Stem
                : "file:" + x.Assembly.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var family in families)
        {
            var members = family.ToList();

            if (members.Count < 2 || members.Any(m => m.Split is null))
            {
                kept.AddRange(members.Select(m => m.Assembly));
                continue;
            }

            var pick = members
                .Where(m => m.Split!.Value.Version.CompareTo(gameVersion) <= 0)
                .OrderBy(m => m.Split!.Value.Version)
                .LastOrDefault();

            if (pick.Assembly is not null)
                kept.Add(pick.Assembly);
        }

        return kept;
    }

    private static bool InLoadedPlatformFolder(string path, string loadedPlatformFolder)
    {
        var directory = Path.GetDirectoryName(path);
        return directory is not null
            && string.Equals(Path.GetFileName(directory), loadedPlatformFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<(string DeclaringType, string MemberName), RetargetCandidate> BuildAssemblyRetargetMap(
        string assemblyPath,
        string moduleVersion,
        IReadOnlyList<MissingApiReference> rawItems,
        IReadOnlyList<IndexedAssembly> activeAssemblies)
    {
        var map = new Dictionary<(string DeclaringType, string MemberName), RetargetCandidate>();
        var referenced = rawItems.Where(r => r.MemberName is not null)
            .Select(r => r.ReferencedAssembly).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var group in activeAssemblies.Where(a => referenced.Contains(a.Name))
                     .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            var paths = group.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // Several loadable copies are a shadowing question. Do not choose a repair target for the user.
            if (paths.Count != 1) continue;
            try
            {
                var plan = ApiCallRetargeting.Inspect(assemblyPath, paths[0]);
                if (plan is null) continue;
                bool Matches(ApiRetargetCall call, MissingApiReference raw) =>
                    raw.MemberName is not null && call.OriginalSignature.Contains(
                        raw.TypeName.Replace('+', '/') + "::" + raw.MemberName + "(", StringComparison.Ordinal);
                var matching = rawItems.Where(raw => plan.Calls.Any(call => Matches(call, raw))).ToList();
                if (matching.Count == 0) continue;

                using var replacementStream = new MemoryStream(ApiCallRetargeting.BuildReplacement(plan), writable: false);
                using var replacement = Mono.Cecil.ModuleDefinition.ReadModule(replacementStream);
                var remainingTypes = replacement.GetTypeReferences().Select(type => type.FullName.Replace('/', '+'))
                    .ToHashSet(StringComparer.Ordinal);
                var removedTypes = rawItems.Where(raw => raw.Kind == MissingApiKind.TypeMissing
                        && !remainingTypes.Contains(raw.TypeName))
                    .Select(raw => raw.TypeName).ToHashSet(StringComparer.Ordinal);

                Action apply = () => ApiCallRetargeting.Apply(plan);
                foreach (var raw in matching)
                {
                    var type = raw.TypeName.Replace('+', '/') + "::";
                    var signatures = plan.Calls.Where(call => Matches(call, raw))
                        .Select(call => call.TargetSignature.Replace(type, string.Empty))
                        .Distinct(StringComparer.Ordinal);
                    map[(raw.TypeName, raw.MemberName!)] = new RetargetCandidate(
                        string.Join("; ", signatures), group.Key, GetBackupPath(assemblyPath, moduleVersion), apply)
                    {
                        RemovedTypeReferences = removedTypes
                    };
                }

                // One atomic rewrite per source DLL. A later check can offer a different target assembly;
                // several stale plans must never each overwrite the same source from its earlier bytes.
                return map;
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException
                or InvalidOperationException or UnauthorizedAccessException or Mono.Cecil.AssemblyResolutionException)
            {
                // The metadata finding remains visible even when no repair can safely be prepared.
            }
        }
        return map;
    }

    public static string GetBackupPath(string assemblyPath, string moduleVersion) =>
        FileReplacementInstaller.BackupPathFor(assemblyPath);
}
