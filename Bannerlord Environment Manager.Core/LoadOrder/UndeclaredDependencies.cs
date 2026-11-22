using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

public sealed record UndeclaredEvidence(string TypeFullName, string DefiningAssembly, string ReferencingAssembly)
{
    public string Describe() => Strings.Current.Format(
        "Core.LoadOrder.UndeclaredDependency.EvidenceLine", TypeFullName, DefiningAssembly, ReferencingAssembly);
}

// One direction only: the module whose code names a type the other module defines. The edge is never
// added to the constraint graph, because the load order model is that what a manifest does not declare
// is not constrained. It is shown so the user can decide, and the wording says detected, never declared.
public sealed record UndeclaredDependency(
    ModuleId FromId,
    ModuleId ToId,
    IReadOnlyList<UndeclaredEvidence> Evidence)
{
    public string Describe() =>
        Strings.Current.Format("Core.LoadOrder.UndeclaredDependency.Uses", ToId, DescribeEvidence());

    public string DescribeFromTheOtherSide() =>
        Strings.Current.Format("Core.LoadOrder.UndeclaredDependency.UsedBy", FromId, DescribeEvidence());

    private string DescribeEvidence()
    {
        var first = Evidence.Count == 0 ? string.Empty : Evidence[0].Describe();

        return Evidence.Count switch
        {
            0 => Strings.Current["Core.LoadOrder.UndeclaredDependency.NoEvidence"],
            1 => Strings.Current.Format("Core.LoadOrder.UndeclaredDependency.OneEvidence", first),
            _ => Strings.Current.Plural("Core.LoadOrder.UndeclaredDependency.TypeCount", Evidence.Count, first)
        };
    }
}

public sealed record UndeclaredDependencyScan(IReadOnlyList<UndeclaredDependency> Edges, string? Error = null)
{
    public static UndeclaredDependencyScan Empty { get; } = new([]);

    public bool Failed => Error is not null;
}

// If module A's assembly references a type defined in module B's assembly, A depends on B whether or
// not its manifest says so. Almost every such reference is noise: on a 195-module install the raw
// count is 4312 edges, and a check whose expected output on a healthy install is large is miscalibrated.
// Three exclusions cut it to what is worth reading, and each of them names a real class of false
// positive rather than a threshold.
public static class UndeclaredDependencies
{
    // A module cannot own these namespaces. Mods ILMerge shims of framework types into their own
    // assemblies, and one such copy of System.ObsoleteAttribute made 25 unrelated mods look like they
    // depended on the module that shipped it.
    private static readonly string[] FrameworkNamespaces = ["System.", "Microsoft.", "Mono."];

    public static UndeclaredDependencyScan Find(AssemblyIndex index, IReadOnlyList<ModuleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(entries);

        if (index.Failed)
            return new UndeclaredDependencyScan([], index.Error);

        var known = new HashSet<ModuleId>(entries.Select(e => e.Id));

        var moduleAssemblies = index.Assemblies
            .Where(a => a.Origin == AssemblyOrigin.Module && a.ModuleId is { } id && known.Contains(id))
            .ToList();

        if (moduleAssemblies.Count == 0)
            return UndeclaredDependencyScan.Empty;

        // Types the game defines resolve to the game's own copy, whatever a module also carries.
        var gameTypes = new HashSet<string>(
            index.Assemblies.Where(a => a.Origin == AssemblyOrigin.Game).SelectMany(a => a.DefinedTypeFullNames),
            StringComparer.Ordinal);

        var definers = new Dictionary<string, List<IndexedAssembly>>(StringComparer.Ordinal);

        foreach (var assembly in moduleAssemblies)
        {
            foreach (var type in assembly.DefinedTypeFullNames)
            {
                if (!definers.TryGetValue(type, out var list))
                    definers[type] = list = [];

                list.Add(assembly);
            }
        }

        var declared = DeclaredPairs(entries);
        var found = new Dictionary<(ModuleId From, ModuleId To), List<UndeclaredEvidence>>();

        foreach (var assembly in moduleAssemblies)
        {
            var from = assembly.ModuleId!.Value;

            foreach (var type in assembly.ReferencedTypeFullNames.Distinct(StringComparer.Ordinal))
            {
                if (gameTypes.Contains(type) || IsFrameworkType(type))
                    continue;

                if (!definers.TryGetValue(type, out var defining))
                    continue;

                // A library many modules ship is a shared library, not an edge to whichever module
                // happens to hold one of the copies: 0Harmony alone has 23 copies on a real
                // install. The count is of modules, so a module shipping two copies of its own
                // library still counts once.
                if (defining.Select(a => a.ModuleId!.Value).Distinct().Count() > 1)
                    continue;

                var definer = defining[0];
                var to = definer.ModuleId!.Value;

                if (to == from || declared.Contains((from, to)))
                    continue;

                if (!found.TryGetValue((from, to), out var evidence))
                    found[(from, to)] = evidence = [];

                evidence.Add(new UndeclaredEvidence(
                    type,
                    Path.GetFileName(definer.Path),
                    Path.GetFileName(assembly.Path)));
            }
        }

        var edges = found
            .Select(pair => new UndeclaredDependency(
                pair.Key.From,
                pair.Key.To,
                [.. pair.Value.OrderBy(e => e.TypeFullName, StringComparer.Ordinal)]))
            .OrderBy(edge => edge.FromId.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => edge.ToId.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new UndeclaredDependencyScan(edges);
    }

    public static IReadOnlyList<UndeclaredDependency> From(UndeclaredDependencyScan scan, ModuleId id)
    {
        ArgumentNullException.ThrowIfNull(scan);

        return [.. scan.Edges.Where(edge => edge.FromId == id)];
    }

    public static IReadOnlyList<UndeclaredDependency> To(UndeclaredDependencyScan scan, ModuleId id)
    {
        ArgumentNullException.ThrowIfNull(scan);

        return [.. scan.Edges.Where(edge => edge.ToId == id)];
    }

    // A declaration either way relates the pair, so nothing here is news to the manifests or to the
    // sort. Incompatibility counts too: the author knows the other module exists.
    private static HashSet<(ModuleId, ModuleId)> DeclaredPairs(IReadOnlyList<ModuleEntry> entries)
    {
        var pairs = new HashSet<(ModuleId, ModuleId)>();

        foreach (var entry in entries)
        {
            foreach (var dependency in entry.Dependencies)
            {
                pairs.Add((entry.Id, dependency.TargetId));
                pairs.Add((dependency.TargetId, entry.Id));
            }
        }

        return pairs;
    }

    private static bool IsFrameworkType(string typeFullName) =>
        FrameworkNamespaces.Any(prefix => typeFullName.StartsWith(prefix, StringComparison.Ordinal));
}
