using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Modules.KnownIssues;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// Ordered so a comparison reads the way the grades do. High and Medium affect play and are findings;
// Low is "several mods touch the method, nothing found that interferes" and stays behind Show Hidden.
public enum PatchOrderGrade
{
    Low,
    Medium,
    High
}

public sealed record PatchFlowStep(
    int Number,
    PatchKind Kind,
    ModuleId ModuleId,
    string ModuleName,
    string PatchMethod,
    int Priority,
    string Effect);

public sealed record PatchOrderProblem(PatchOrderGrade Grade, string Text, IReadOnlyList<ModuleId> Modules);

public sealed record PatchOrderCulprit(ModuleId Id, string Name);

public sealed record PatchOrderFinding(
    PatchOrderGrade Grade,
    string Target,
    IReadOnlyList<ModuleId> Modules,
    IReadOnlyList<string> ModuleNames,
    string Headline,
    IReadOnlyList<PatchFlowStep> Flow,
    IReadOnlyList<PatchOrderProblem> Problems,
    string Fix,
    IReadOnlyList<PatchOrderCulprit> CanDisable,
    bool OrderIsAssumed)
{
    // Stable across runs and languages, so an acceptance survives a rescan.
    public string Subject => $"{Target}|{string.Join("+", Modules.Select(m => m.Value).Order(StringComparer.OrdinalIgnoreCase))}";
}

public sealed record PatchOrderReport(
    IReadOnlyList<PatchOrderFinding> Findings,
    IReadOnlyList<string> Problems,
    int MethodsPatchedByMods,
    string Summary,
    string Scope)
{
    public int High => Findings.Count(f => f.Grade == PatchOrderGrade.High);

    public int Medium => Findings.Count(f => f.Grade == PatchOrderGrade.Medium);

    public int Low => Findings.Count(f => f.Grade == PatchOrderGrade.Low);
}

// What the mod HarmonyPatchScanner reports, found without that mod and without loading any mod's code.
// For each game method that two or more mods patch: the order Harmony runs the patches in, what each
// bool prefix returns (read from its IL), who is skipped by it, who has the final say on __result, and
// a grade.
//
// Order is Harmony's: per kind, higher priority first, then the order the patches were registered,
// which is the order they sit in the stored registry. Priority is read from [HarmonyPriority] on the
// patch method or its class; a priority a mod sets at run time and [HarmonyBefore] / [HarmonyAfter]
// are not applied, and a method where one is present says its order is assumed.
//
// A patch a mod applies later (on a campaign start, in a mission) is not in a capture taken at the main
// menu. On the owner's install that hid two of HarmonyPatchScanner's three HIGH methods, so patches a mod
// declares with attributes are read from its assembly and added, and a method holding one says its order
// is assumed, since when the mod applies it is not known. A declared patch behind Prepare or a category
// may never apply, so it is left out: a miss rather than a false accusation.
//
// What it cannot say, and says so: a patch applied by code at run time, outside a capture; and whether a
// patch really writes to a ref parameter it declares needs more than the signature.
//
// No reorder is offered as a fix. Measured on a reference install, the launcher position of a mod and
// the index its patches got agreed for 63 of 76 equal-priority pairs: a mod that patches late (on a
// game start, say) registers after mods that sit below it, so a load order move cannot be shown to
// change the order. Disabling a mod can, and is offered.
public static class PatchOrderAnalysis
{
    private const string LifecycleType = "TaleWorlds.MountAndBlade.MBSubModuleBase";

    private static readonly string[] Libraries =
    [
        "Bannerlord.Harmony", "Bannerlord.ButterLib", "Bannerlord.UIExtenderEx", "Bannerlord.MBOptionScreen", "BEM"
    ];

    public static PatchOrderReport Inspect(
        PatchRegistryLookup lookup,
        AssemblyIndex index,
        IReadOnlyList<ModuleEntry> entries,
        IReadOnlyList<HarmonyPatchTarget>? declared = null,
        // False for a patch its own mod does not apply on this install (a part of RBM that is switched
        // off, RbmSettings). Such a patch is not in the game and takes no part in any finding.
        Func<PatchRecord, bool>? isApplied = null)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(entries);

        var scope = Strings.Current["Core.Diagnostics.PatchOrder.Scope"];

        if (lookup.Registry is not { } registry)
            return new PatchOrderReport([], [], 0, Strings.Current["Core.Diagnostics.PatchOrder.NoCapture"], scope);

        var enabled = entries
            .Where(e => e.IsEnabled)
            .GroupBy(e => e.Id)
            .ToDictionary(g => g.Key, g => g.First());

        var unreadable = new List<string>();
        var shapes = new Dictionary<(ModuleId, string, PatchKind), PatchShape?>();

        PatchShape? Shape(PatchRecord patch)
        {
            var key = (patch.ModuleId, patch.PatchMethod, patch.Kind);

            if (!shapes.TryGetValue(key, out var shape))
                shapes[key] = shape = ReadShape(index, patch, unreadable);

            return shape;
        }

        var findings = new List<PatchOrderFinding>();
        var methods = 0;
        var sequence = 0;

        // The registry order is the registration order, so a patch keeps the position it was stored at.
        var added = Undiscovered(registry, declared ?? []);
        var numbered = registry.Patches.Concat(added).Select(p => (Patch: p, Sequence: sequence++)).ToList();
        var addedSet = added.ToHashSet();

        foreach (var group in numbered
                     .Where(n => n.Patch.AttributedBy is PatchAttribution.AssemblyLocation
                                 && enabled.ContainsKey(n.Patch.ModuleId)
                                 && (isApplied is null || isApplied(n.Patch))
                                 && !string.Equals(n.Patch.TargetTypeFullName, LifecycleType, StringComparison.Ordinal))
                     .GroupBy(n => (n.Patch.TargetTypeFullName, n.Patch.TargetMethodName)))
        {
            var modules = group.Select(n => n.Patch.ModuleId).Distinct().ToList();

            if (modules.Count < 2 || modules.All(m => IsLibrary(m)))
                continue;

            methods++;

            var finding = Analyze(group.Key.TargetTypeFullName, group.Key.TargetMethodName, group.ToList(), modules, enabled, Shape);

            findings.Add(group.Any(n => addedSet.Contains(n.Patch)) ? finding with { OrderIsAssumed = true } : finding);
        }

        var ordered = findings
            .OrderByDescending(f => f.Grade)
            .ThenBy(f => f.Target, StringComparer.Ordinal)
            .ToList();

        var summary = Strings.Current.Format(
            "Core.Diagnostics.PatchOrder.Summary",
            methods,
            ordered.Count(f => f.Grade == PatchOrderGrade.High),
            ordered.Count(f => f.Grade == PatchOrderGrade.Medium),
            ordered.Count(f => f.Grade == PatchOrderGrade.Low),
            unreadable.Count);

        if (lookup.IsStale)
            summary += " " + lookup.Reason;

        return new PatchOrderReport(ordered, unreadable, methods, summary, scope);
    }

    private static List<PatchRecord> Undiscovered(PatchRegistry registry, IReadOnlyList<HarmonyPatchTarget> declared)
    {
        var known = registry.Patches
            .Select(p => (p.TargetTypeFullName, p.TargetMethodName, p.ModuleId, p.PatchMethod))
            .ToHashSet();
        var records = new List<PatchRecord>();

        foreach (var target in declared.Where(t => t is
                     { TargetType: not null, TargetMember: not null, MethodKind: "Normal", GuardedByPrepare: false, Category: null }))
        {
            foreach (var name in target.PatchMethod.Split(", "))
            {
                var method = $"{target.PatchClass}.{name}";

                if (KindFromName(name) is { } kind
                    && known.Add((target.TargetType!, target.TargetMember!, target.ModuleId, method)))
                {
                    records.Add(new PatchRecord(
                        target.TargetType!,
                        target.TargetMember!,
                        target.ModuleId,
                        kind,
                        target.ModuleId.Value,
                        PatchAttribution.AssemblyLocation,
                        method));
                }
            }
        }

        return records;
    }

    private static PatchKind? KindFromName(string name)
    {
        foreach (var kind in new[] { PatchKind.Transpiler, PatchKind.Prefix, PatchKind.Finalizer, PatchKind.Postfix })
        {
            if (name.Contains(kind.ToString(), StringComparison.OrdinalIgnoreCase))
                return kind;
        }

        return null;
    }

    private static bool IsLibrary(ModuleId id) =>
        Libraries.Contains(id.Value, StringComparer.OrdinalIgnoreCase);

    private static PatchShape? ReadShape(AssemblyIndex index, PatchRecord patch, List<string> unreadable)
    {
        var cut = patch.PatchMethod.LastIndexOf('.');

        if (cut <= 0)
            return null;

        var typeName = patch.PatchMethod[..cut];
        var methodName = patch.PatchMethod[(cut + 1)..];

        foreach (var assembly in index.FindDeclaring(typeName).Where(a => a.ModuleId == patch.ModuleId))
        {
            try
            {
                if (PatchBodyReader.Read(assembly.Path, typeName, methodName, patch.Kind) is { } shape)
                    return shape;
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                unreadable.Add($"{assembly.Path}: {ex.Message}");
            }
        }

        return null;
    }

    private sealed record Step(PatchRecord Patch, int Sequence, PatchShape? Shape)
    {
        public int Priority => PatchBodyReader.EffectivePriority(Shape);
    }

    private static PatchOrderFinding Analyze(
        string type,
        string method,
        List<(PatchRecord Patch, int Sequence)> patches,
        List<ModuleId> modules,
        Dictionary<ModuleId, ModuleEntry> enabled,
        Func<PatchRecord, PatchShape?> shape)
    {
        var steps = patches.Select(p => new Step(p.Patch, p.Sequence, shape(p.Patch))).ToList();

        List<Step> Of(PatchKind kind) =>
            [.. steps.Where(s => s.Patch.Kind == kind).OrderByDescending(s => s.Priority).ThenBy(s => s.Sequence)];

        var transpilers = Of(PatchKind.Transpiler);
        var prefixes = Of(PatchKind.Prefix);
        var finalizers = Of(PatchKind.Finalizer);

        // Void postfixes run first, then the ones that pass a value through and replace __result.
        var postfixes = Of(PatchKind.Postfix)
            .OrderBy(s => s.Shape?.PassThrough == true ? 1 : 0)
            .ToList();

        string Name(ModuleId id) => enabled[id].DisplayName;

        var problems = new List<PatchOrderProblem>();

        // Prefixes. A bool prefix that returns false skips the original, the code of every transpiler,
        // and every later prefix that affects the original.
        foreach (var prefix in prefixes.DistinctBy(p => (p.Patch.ModuleId, p.Patch.PatchMethod)))
        {
            // A prefix BEM could not read is assumed able to skip: a row must not clear what it could not look at.
            var returned = prefix.Shape?.PrefixReturn ?? PrefixReturn.Unknown;

            if (returned is PrefixReturn.NotBool or PrefixReturn.AlwaysTrue)
                continue;

            var later = prefixes.Skip(prefixes.IndexOf(prefix) + 1)
                .Where(p => p.Patch.ModuleId != prefix.Patch.ModuleId && (p.Shape is null || p.Shape.AffectsOriginal))
                .ToList();
            var transpiled = transpilers.Where(t => t.Patch.ModuleId != prefix.Patch.ModuleId).ToList();

            var victims = later.Select(p => p.Patch.ModuleId)
                .Concat(transpiled.Select(t => t.Patch.ModuleId))
                .Distinct()
                .ToList();

            var who = Name(prefix.Patch.ModuleId);
            var cut = victims.Count == 0
                ? string.Empty
                : string.Join(
                    ", ",
                    later.Select(p => Strings.Current.Format("Core.Diagnostics.PatchOrder.Victim.Prefix", Name(p.Patch.ModuleId)))
                        .Concat(transpiled.Select(t => Strings.Current.Format("Core.Diagnostics.PatchOrder.Victim.Transpiler", Name(t.Patch.ModuleId))))
                        .Distinct());

            if (returned == PrefixReturn.AlwaysFalse)
            {
                problems.Add(victims.Count > 0
                    ? new PatchOrderProblem(
                        PatchOrderGrade.High,
                        Strings.Current.Format("Core.Diagnostics.PatchOrder.Problem.AlwaysFalse", who, cut),
                        [prefix.Patch.ModuleId, .. victims])
                    : new PatchOrderProblem(
                        PatchOrderGrade.Low,
                        Strings.Current.Format("Core.Diagnostics.PatchOrder.Problem.AlwaysFalseAlone", who),
                        [prefix.Patch.ModuleId]));
            }
            else
            {
                var can = returned == PrefixReturn.Unknown
                    ? Strings.Current["Core.Diagnostics.PatchOrder.Problem.Unreadable"]
                    : string.Empty;

                problems.Add(victims.Count > 0
                    ? new PatchOrderProblem(
                        PatchOrderGrade.Medium,
                        Strings.Current.Format("Core.Diagnostics.PatchOrder.Problem.MaySkip", who, cut) + can,
                        [prefix.Patch.ModuleId, .. victims])
                    : new PatchOrderProblem(
                        PatchOrderGrade.Low,
                        Strings.Current.Format("Core.Diagnostics.PatchOrder.Problem.MaySkipAlone", who) + can,
                        [prefix.Patch.ModuleId]));
            }
        }

        // __result. Every patch that can assign it runs in sequence and the last one has the final say.
        var writers = prefixes
            .Concat(postfixes)
            .Where(s => s.Shape is { ChangesResult: true } or { PassThrough: true })
            .ToList();
        var writerModules = writers.Select(w => w.Patch.ModuleId).Distinct().ToList();

        // Postfixes that can only move the result the same way (all to false, all to true) leave the same
        // result in any order, so there is nothing for the owner to decide.
        var resultCommutes = writers.All(w => w.Patch.Kind == PatchKind.Postfix && w.Shape is { PassThrough: false })
                             && writers.Select(w => w.Shape!.KindOf("__result")).Distinct().ToList() is [not WriteKind.Other];

        if (writerModules.Count > 1 && !resultCommutes)
        {
            var last = writers[^1].Patch.ModuleId;
            var names = string.Join(", ", writers.Select(w => Name(w.Patch.ModuleId)).Distinct());

            problems.Add(new PatchOrderProblem(
                PatchOrderGrade.Medium,
                Strings.Current.Format("Core.Diagnostics.PatchOrder.Problem.FinalSay", names, Name(last)),
                writerModules));
        }

        // A ref argument two mods can both rewrite: each sees what the one before it left.
        foreach (var argument in steps
                     .Where(s => s.Shape is not null)
                     .SelectMany(s => s.Shape!.ChangedArguments.Select(a => (Argument: a, Step: s)))
                     .GroupBy(x => x.Argument, StringComparer.Ordinal)
                     .Where(g => g.Select(x => x.Step.Patch.ModuleId).Distinct().Count() > 1)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var involved = argument.Select(x => x.Step.Patch.ModuleId).Distinct().ToList();

            // Writes that only go one way (always true, always false, or additions) end the same in any order.
            if (argument.Select(x => x.Step.Shape!.KindOf(argument.Key)).Distinct().ToList() is [not WriteKind.Other])
                continue;

            problems.Add(new PatchOrderProblem(
                PatchOrderGrade.Medium,
                Strings.Current.Format(
                    "Core.Diagnostics.PatchOrder.Problem.Argument",
                    argument.Key,
                    string.Join(", ", involved.Select(Name))),
                involved));
        }

        if (problems.Count == 0)
        {
            problems.Add(new PatchOrderProblem(
                PatchOrderGrade.Low,
                Strings.Current["Core.Diagnostics.PatchOrder.Problem.Coexisting"],
                modules));
        }

        var grade = problems.Max(p => p.Grade);

        var flow = new List<PatchFlowStep>();
        var number = 1;

        foreach (var step in transpilers.Concat(prefixes).Concat(postfixes).Concat(finalizers))
        {
            flow.Add(new PatchFlowStep(
                number++,
                step.Patch.Kind,
                step.Patch.ModuleId,
                Name(step.Patch.ModuleId),
                step.Patch.PatchMethod,
                step.Priority,
                Effect(step)));
        }

        var assumed = steps.Any(s => s.Shape is { HasBeforeOrAfter: true } || s.Shape is null);
        var affected = problems
            .Where(p => p.Grade == grade && grade != PatchOrderGrade.Low)
            .SelectMany(p => p.Modules)
            .Distinct()
            .ToList();

        var canDisable = affected
            .Where(id => !IsLibrary(id) && !enabled[id].IsOfficial)
            .Select(id => new PatchOrderCulprit(id, Name(id)))
            .ToList();

        return new PatchOrderFinding(
            grade,
            $"{type}.{method}",
            modules,
            [.. modules.Select(Name)],
            Strings.Current.Format(
                grade switch
                {
                    PatchOrderGrade.High => "Core.Diagnostics.PatchOrder.Headline.High",
                    PatchOrderGrade.Medium => "Core.Diagnostics.PatchOrder.Headline.Medium",
                    _ => "Core.Diagnostics.PatchOrder.Headline.Low"
                },
$"{type}.{method}", string.Join(", ", modules.Select(Name))),
            flow,
            [.. problems.OrderByDescending(p => p.Grade)],
            FixFor(grade, problems, canDisable.Count, steps, Name),
            canDisable,
            assumed);
    }

    private static string Effect(Step step)
    {
        if (step.Shape is null)
            return Strings.Current["Core.Diagnostics.PatchOrder.Effect.Unread"];

        return step.Patch.Kind switch
        {
            PatchKind.Prefix => step.Shape.PrefixReturn switch
            {
                PrefixReturn.AlwaysFalse => Strings.Current["Core.Diagnostics.PatchOrder.Effect.AlwaysFalse"],
                PrefixReturn.AlwaysTrue => Strings.Current["Core.Diagnostics.PatchOrder.Effect.AlwaysTrue"],
                PrefixReturn.NotBool => Strings.Current["Core.Diagnostics.PatchOrder.Effect.Void"],
                _ => Strings.Current["Core.Diagnostics.PatchOrder.Effect.Conditional"]
            },
            _ => step.Shape.ChangesResult || step.Shape.PassThrough
                ? Strings.Current["Core.Diagnostics.PatchOrder.Effect.Result"]
                : step.Shape.ChangedArguments.Count > 0
                    ? Strings.Current.Format("Core.Diagnostics.PatchOrder.Effect.Arguments", string.Join(", ", step.Shape.ChangedArguments))
                    : Strings.Current["Core.Diagnostics.PatchOrder.Effect.Observes"]
        };
    }

    // A fix when one exists, and the reason when none does. Disabling a mod is the one remedy
    // that provably changes the outcome. A reorder is never claimed: priority decides before order does,
    // and the registration order is only loosely tied to the load order (see the type's comment).
    private static string FixFor(
        PatchOrderGrade grade,
        List<PatchOrderProblem> problems,
        int disableable,
        List<Step> steps,
        Func<ModuleId, string> name)
    {
        if (grade == PatchOrderGrade.Low)
            return Strings.Current["Core.Diagnostics.PatchOrder.Fix.Nothing"];

        var text = new List<string>();

        if (disableable > 0)
            text.Add(Strings.Current["Core.Diagnostics.PatchOrder.Fix.Disable"]);
        else
            text.Add(Strings.Current["Core.Diagnostics.PatchOrder.Fix.NoneToDisable"]);

        var prioritised = steps
            .Where(s => s.Shape?.Priority is not null && s.Priority != 400)
            .Select(s => Strings.Current.Format("Core.Diagnostics.PatchOrder.Fix.PriorityOf", name(s.Patch.ModuleId), s.Priority))
            .Distinct()
            .ToList();

        text.Add(prioritised.Count > 0
            ? Strings.Current.Format("Core.Diagnostics.PatchOrder.Fix.NoReorderPriority", string.Join(", ", prioritised))
            : Strings.Current["Core.Diagnostics.PatchOrder.Fix.NoReorder"]);

        return string.Join(" ", text);
    }
}
