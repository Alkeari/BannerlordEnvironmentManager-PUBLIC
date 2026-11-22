using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.LoadOrder;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public sealed record HarmonyPatchSite(
    ModuleId ModuleId,
    string ModuleName,
    string TargetType,
    string TargetMember,
    string Kind,
    string AssemblyPath);

public sealed record HarmonyPatchOverlap(
    string TargetType,
    string TargetMember,
    IReadOnlyList<HarmonyPatchSite> Sites)
{
    public string Target => $"{TargetType}.{TargetMember}";

    public string ShortTarget => $"{TargetType[(TargetType.LastIndexOfAny(['.', '+', '/']) + 1)..]}.{TargetMember}";

    public IReadOnlyList<ModuleId> Modules =>
        [.. Sites.Select(s => s.ModuleId).Distinct().OrderBy(m => m.Value, StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<string> ModuleNames =>
        [.. Sites.GroupBy(s => s.ModuleId).Select(g => g.First().ModuleName).Order(StringComparer.OrdinalIgnoreCase)];

    // Two postfixes stack the way Harmony intends. A transpiler rewrites the method's IL, so any other
    // patch on the same method is working on code that is no longer the game's.
    public bool HasTranspiler => Sites.Any(s => s.Kind.Equals("Transpiler", StringComparison.OrdinalIgnoreCase));
}

public sealed record HarmonyPatchOverlapReport(
    int AssembliesScanned,
    int TargetsSeen,
    IReadOnlyList<HarmonyPatchOverlap> Overlaps,
    IReadOnlyList<HarmonyPatchProblem> Problems)
{
    public static HarmonyPatchOverlapReport CouldNotLook() => new(0, 0, [], []);
}

// Which game methods two or more enabled mods both patch, read from the mods' own assemblies before the
// game starts. The crash this exists for (0xC0000005 inside PartyUpgraderCampaignBehavior.
// GetPossibleUpgradeTargets, with TroopTrainingRequirement's postfix and BanditMilitiasRedux's transpiler
// both on it) left nothing but an unhandled-exception line, and every mod involved was working as written.
//
// Two ways a mod names its target are read, and both are metadata and IL only, no mod code runs:
// the HarmonyPatch attributes (through HarmonyPatchTargets) and the imperative form, a method that
// resolves the target with AccessTools or Type.GetMethod and then calls Harmony.Patch, which is how
// TroopTrainingRequirement patches. A target a mod computes at run time is invisible here, and this
// reports an overlap as a fact about two patches, never as a verdict that they conflict.
public static class HarmonyPatchOverlaps
{
    private const string HarmonyType = "HarmonyLib.Harmony";

    private const string AccessToolsType = "HarmonyLib.AccessTools";

    // ButterLib wraps every module's lifecycle callbacks, so owners in the dozens here are the wrapper.
    private static readonly string[] LifecycleTypes = ["TaleWorlds.MountAndBlade.MBSubModuleBase"];

    private static readonly string[] AccessToolsLookups =
        ["Method", "DeclaredMethod", "PropertyGetter", "PropertySetter", "DeclaredPropertyGetter", "DeclaredPropertySetter"];

    private static readonly string[] TypeLookups = ["GetMethod", "GetProperty"];

    private static readonly string[] Kinds = ["Transpiler", "Prefix", "Postfix", "Finalizer"];

    public static HarmonyPatchOverlapReport Inspect(
        AssemblyIndex index,
        IReadOnlyList<ModuleEntry> entries,
        string? loadedPlatformFolder = null,
        ModuleVersion gameVersion = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(entries);

        if (index.Failed)
            return HarmonyPatchOverlapReport.CouldNotLook();

        var enabled = entries.Where(e => e.IsEnabled).Select(e => e.Id).ToHashSet();
        var names = entries.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First().DisplayName);

        var scanned = index.Assemblies
            .Where(a => a.Origin == AssemblyOrigin.Module && a.ModuleId is { } id && enabled.Contains(id))
            .ToList();

        // With the game's platform folder known, only what the engine opens is read: a mod shipping a
        // build per game version (MCM carries twenty) leaves the others beside it, and they never load, so
        // reading them cost seconds at Launch and could only report patches that never apply.
        if (!string.IsNullOrWhiteSpace(loadedPlatformFolder))
        {
            scanned = scanned
                .Where(a => string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(a.Path)), loadedPlatformFolder, StringComparison.OrdinalIgnoreCase))
                .ToList();

            scanned = MissingGameApis.LoadedForGame(scanned, gameVersion);
        }

        var perAssembly = new (List<HarmonyPatchSite> Sites, HarmonyPatchProblem? Problem)[scanned.Count];

        // Each assembly is read on its own with its own Cecil module, so they read side by side; half the
        // processors keeps the machine usable while Launch waits.
        Parallel.For(
            0,
            scanned.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) },
            i =>
            {
                var assembly = scanned[i];
                var module = assembly.ModuleId!.Value;
                var name = names.TryGetValue(module, out var display) ? display : module.Value;
                var local = new List<HarmonyPatchSite>();

                try
                {
                    foreach (var raw in SitesCache.GetOrRead(assembly.Path, () => ReadSites(assembly)))
                        local.Add(new HarmonyPatchSite(module, name, raw.TargetType, raw.TargetMember, raw.Kind, assembly.Path));

                    perAssembly[i] = (local, null);
                }
                // Reading a method body of an obfuscated or damaged assembly throws whatever the decoder ran into
                // (an index out of range and an overflow were both seen on the owner's play install, 2026-11-11). The assembly
                // is given up at the first one and told as not read, and the check goes on with the rest.
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    perAssembly[i] = ([], new HarmonyPatchProblem(module, assembly.Path, ex.Message));
                }
            });

        var sites = new List<HarmonyPatchSite>();
        var problems = new List<HarmonyPatchProblem>();

        foreach (var (found, problem) in perAssembly)
        {
            sites.AddRange(found);

            if (problem is not null)
                problems.Add(problem);
        }

        var byTarget = sites
            .Where(s => !LifecycleTypes.Contains(s.TargetType, StringComparer.Ordinal))
            .GroupBy(s => (Type: Normalize(s.TargetType), s.TargetMember))
            .ToList();

        var overlaps = byTarget
            .Where(g => g.Select(s => s.ModuleId).Distinct().Count() > 1)
            .Select(g => new HarmonyPatchOverlap(g.First().TargetType, g.Key.TargetMember, [.. g.DistinctBy(s => (s.ModuleId, s.Kind))]))
            .OrderByDescending(o => o.HasTranspiler)
            .ThenByDescending(o => o.Modules.Count)
            .ThenBy(o => o.Target, StringComparer.Ordinal)
            .ToList();

        return new HarmonyPatchOverlapReport(scanned.Count, byTarget.Count, overlaps, problems);
    }

    // A nested type is written with a slash in IL and a plus sign or a dot elsewhere.
    private static string Normalize(string type) => type.Replace('/', '.').Replace('+', '.');

    private static string? KindOf(string text) =>
        Kinds.FirstOrDefault(kind => text.Contains(kind, StringComparison.OrdinalIgnoreCase));

    private static readonly FileResultCache<IReadOnlyList<(string TargetType, string TargetMember, string Kind)>> SitesCache = new();

    // What one assembly patches, owner-neutral so it can be remembered between launches.
    private static IReadOnlyList<(string TargetType, string TargetMember, string Kind)> ReadSites(IndexedAssembly assembly)
    {
        var sites = new List<(string, string, string)>();

        foreach (var declared in HarmonyPatchTargets.DeclaredPatches(assembly))
            sites.Add((declared.TargetType, declared.TargetMember, KindOf(declared.PatchMethod) ?? "Patch"));

        foreach (var imperative in ReadImperative(assembly.Path, assembly.ModuleId!.Value, string.Empty))
            sites.Add((imperative.TargetType, imperative.TargetMember, imperative.Kind));

        return sites;
    }

    private static List<HarmonyPatchSite> ReadImperative(string path, ModuleId module, string moduleName)
    {
        var found = new List<HarmonyPatchSite>();

        using var definition = ModuleDefinition.ReadModule(path, new ReaderParameters { ReadingMode = ReadingMode.Deferred });

        if (!definition.AssemblyReferences.Any(r => r.Name.Equals("0Harmony", StringComparison.OrdinalIgnoreCase)))
            return found;

        foreach (var type in definition.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;

                var instructions = method.Body.Instructions;

                if (!instructions.Any(IsHarmonyPatchCall))
                    continue;

                for (var i = 0; i < instructions.Count; i++)
                {
                    if (!IsLookup(instructions[i]))
                        continue;

                    var (target, member) = ArgumentsBefore(instructions, i);

                    if (target is null || member is null)
                        continue;

                    // Nothing in the module's own assembly is a game method.
                    if (target.Scope?.Name is { } scope
                        && scope.Equals(definition.Assembly.Name.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    found.Add(new HarmonyPatchSite(module, moduleName, target.FullName, member, KindAfter(instructions, i), path));
                }
            }
        }

        return found;
    }

    private static bool IsHarmonyPatchCall(Instruction instruction) =>
        instruction.Operand is MethodReference { Name: "Patch", DeclaringType.FullName: HarmonyType };

    private static bool IsLookup(Instruction instruction)
    {
        if (instruction.Operand is not MethodReference reference)
            return false;

        return (reference.DeclaringType.FullName == AccessToolsType && AccessToolsLookups.Contains(reference.Name, StringComparer.Ordinal))
            || (reference.DeclaringType.FullName == "System.Type" && TypeLookups.Contains(reference.Name, StringComparer.Ordinal));
    }

    // The name is the nearest string before the call; the type is the nearest type token before that
    // string, so a parameter type listed after the name is never taken for the declaring type.
    private static (TypeReference? Type, string? Member) ArgumentsBefore(
        Mono.Collections.Generic.Collection<Instruction> instructions, int call)
    {
        TypeReference? type = null;
        string? member = null;
        var stringAt = -1;

        for (var i = call - 1; i >= 0 && call - i <= 16; i--)
        {
            if (instructions[i].OpCode == OpCodes.Ldstr)
            {
                member = (string)instructions[i].Operand;
                stringAt = i;
                break;
            }
        }

        for (var i = stringAt - 1; stringAt >= 0 && i >= 0 && stringAt - i <= 4; i--)
        {
            if (instructions[i].OpCode == OpCodes.Ldtoken && instructions[i].Operand is TypeReference reference)
            {
                type = reference;
                break;
            }
        }

        var name = ((MethodReference)instructions[call].Operand).Name;

        if (member is not null && name.EndsWith("PropertyGetter", StringComparison.Ordinal))
            member = "get_" + member;
        else if (member is not null && name.EndsWith("PropertySetter", StringComparison.Ordinal))
            member = "set_" + member;

        return (type, member);
    }

    // The patch method names handed to the HarmonyMethod built before the Patch call that follows.
    private static string KindAfter(Mono.Collections.Generic.Collection<Instruction> instructions, int lookup)
    {
        for (var i = lookup + 1; i < instructions.Count; i++)
        {
            if (IsHarmonyPatchCall(instructions[i]))
                break;

            if (instructions[i].OpCode == OpCodes.Ldstr && KindOf((string)instructions[i].Operand) is { } kind)
                return kind;
        }

        return "Patch";
    }
}
