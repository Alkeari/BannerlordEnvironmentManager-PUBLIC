using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public sealed record PatchCollision(
    string TargetTypeFullName,
    string TargetMethodName,
    IReadOnlyList<ModuleId> Modules,
    IReadOnlyList<PatchRecord> Patches,
    bool IsContended,
    string Why)
{
    public string Target => $"{TargetTypeFullName}.{TargetMethodName}";
}

public sealed record PatchCollisionExclusion(string Rule, int Methods, string Why);

public sealed record PatchCollisionReport(
    int PatchedMethods,
    int RawCollisions,
    int Excluded,
    int Reported,
    int Contended,
    IReadOnlyList<PatchCollision> Collisions,
    IReadOnlyList<PatchCollisionExclusion> Exclusions,
    string Summary);

// Grouped by target method, listing only methods two or more DISTINCT modules patch.
//
// The exclusion mechanism exists because the raw number is not a finding. On the reference install
// Harmony reports 144 methods with multiple owners, and the measured composition of that number is
// mostly infrastructure working as designed: 22 are MBSubModuleBase lifecycle methods that
// ButterLib's SubModuleWrappers2 patches once per module (13 or more owners each), and the bulk of
// the rest are UIExtenderEx transpilers emitted into dynamic assemblies at runtime over shared view
// models - TaleWorlds.Library.ViewModel.OnFinalize alone carries 23 of them. About 46% of the raw
// count is that noise.
//
// A check whose expected output on a healthy install is a long list is miscalibrated, so this
// reports four numbers rather than one and puts the contended ones first. Every tier is counted and
// every exclusion is named with how much it removed, so the first real capture recalibrates it
// instead of the rule being taken on faith.
public static class PatchCollisions
{
    public const string LifecycleRule = "submodule-lifecycle";

    public const string GeneratedRule = "runtime-generated";

    // ButterLib wraps every module's lifecycle callbacks. Owners in the dozens here are the wrapper,
    // not a contention between mods.
    private static readonly string[] LifecycleTypes =
    [
        "TaleWorlds.MountAndBlade.MBSubModuleBase"
    ];

    public static PatchCollisionReport Build(PatchRegistry? registry)
    {
        if (registry is null)
        {
            return new PatchCollisionReport(
                0, 0, 0, 0, 0, [], [],
                Strings.Current["Core.Diagnostics.PatchCollisions.NoRegistry"]);
        }

        var byMethod = registry.Patches
            .GroupBy(p => (p.TargetTypeFullName, p.TargetMethodName))
            .ToList();

        var raw = new List<IGrouping<(string Type, string Method), PatchRecord>>();

        foreach (var method in byMethod)
        {
            // A patch that could not be tied to a module says nothing about who contends with whom,
            // so it never makes a collision on its own.
            if (method.Where(p => !p.ModuleId.IsEmpty).Select(p => p.ModuleId).Distinct().Count() > 1)
                raw.Add(method);
        }

        var lifecycle = new List<string>();
        var generated = new List<string>();
        var kept = new List<PatchCollision>();

        foreach (var method in raw)
        {
            if (LifecycleTypes.Contains(method.Key.Type, StringComparer.Ordinal))
            {
                lifecycle.Add($"{method.Key.Type}.{method.Key.Method}");
                continue;
            }

            // A patch Harmony could only attribute from its owner string came out of a dynamic
            // assembly with no location. It stays in the registry, because R4 still wants to know a
            // module has code on the faulting method, but it is not on its own evidence that two
            // mods contend for a method.
            var attributed = method
                .Where(p => p.AttributedBy is PatchAttribution.AssemblyLocation && !p.ModuleId.IsEmpty)
                .Select(p => p.ModuleId)
                .Distinct()
                .Count();

            if (attributed < 2)
            {
                generated.Add($"{method.Key.Type}.{method.Key.Method}");
                continue;
            }

            kept.Add(Describe(method));
        }

        var exclusions = new List<PatchCollisionExclusion>();

        if (lifecycle.Count > 0)
        {
            exclusions.Add(new PatchCollisionExclusion(
                LifecycleRule,
                lifecycle.Count,
                Strings.Current["Core.Diagnostics.PatchCollisions.Exclusion.Lifecycle"]));
        }

        if (generated.Count > 0)
        {
            exclusions.Add(new PatchCollisionExclusion(
                GeneratedRule,
                generated.Count,
                Strings.Current["Core.Diagnostics.PatchCollisions.Exclusion.Generated"]));
        }

        var ordered = kept
            .OrderByDescending(c => c.IsContended)
            .ThenByDescending(c => c.Modules.Count)
            .ThenBy(c => c.Target, StringComparer.Ordinal)
            .ToList();

        var contended = ordered.Count(c => c.IsContended);

        return new PatchCollisionReport(
            byMethod.Count,
            raw.Count,
            lifecycle.Count + generated.Count,
            ordered.Count,
            contended,
            ordered,
            exclusions,
            Summarize(byMethod.Count, raw.Count, lifecycle.Count + generated.Count, ordered.Count, contended));
    }

    private static PatchCollision Describe(IGrouping<(string Type, string Method), PatchRecord> method)
    {
        var patches = method
            .OrderBy(p => (int)p.Kind)
            .ThenBy(p => p.ModuleId.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var modules = patches
            .Where(p => !p.ModuleId.IsEmpty)
            .Select(p => p.ModuleId)
            .Distinct()
            .OrderBy(m => m.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var transpilers = Distinct(patches, PatchKind.Transpiler);
        var prefixes = Distinct(patches, PatchKind.Prefix);

        // Harmony's composition model is that postfixes and finalizers stack: several of them on one
        // method is the design working. Two transpilers rewrite the same IL, and two prefixes can
        // each skip the original. Those are the pairs worth a user's attention.
        if (transpilers > 1)
        {
            return new PatchCollision(
                method.Key.Type, method.Key.Method, modules, patches, true,
                Strings.Current.Format("Core.Diagnostics.PatchCollisions.Why.MultipleTranspilers", transpilers));
        }

        if (transpilers == 1 && modules.Count > 1)
        {
            return new PatchCollision(
                method.Key.Type, method.Key.Method, modules, patches, true,
                Strings.Current["Core.Diagnostics.PatchCollisions.Why.TranspilerAndOther"]);
        }

        if (prefixes > 1)
        {
            return new PatchCollision(
                method.Key.Type, method.Key.Method, modules, patches, true,
                Strings.Current.Format("Core.Diagnostics.PatchCollisions.Why.MultiplePrefixes", prefixes));
        }

        return new PatchCollision(
            method.Key.Type, method.Key.Method, modules, patches, false,
            Strings.Current.Format("Core.Diagnostics.PatchCollisions.Why.Stacking", modules.Count));
    }

    private static int Distinct(IReadOnlyList<PatchRecord> patches, PatchKind kind) =>
        patches.Where(p => p.Kind == kind).Select(p => p.ModuleId).Distinct().Count();

    private static string Summarize(int methods, int raw, int excluded, int reported, int contended)
    {
        if (raw == 0)
            return Strings.Current.Plural("Core.Diagnostics.PatchCollisions.Summary.NoneRaw", methods);

        var head = Strings.Current.Plural("Core.Diagnostics.PatchCollisions.Summary.Head", methods, raw, excluded);

        return contended == 0
            ? head + Strings.Current.Format("Core.Diagnostics.PatchCollisions.Summary.NoneContended", reported)
            : head + Strings.Current.Format("Core.Diagnostics.PatchCollisions.Summary.SomeContended", reported, contended);
    }
}
