using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

public enum DependencyClosureKind
{
    EnableWhatItNeeds,
    DisableWhatNeedsIt
}

// The closure of one module over declared required dependencies, in one direction or the other, with
// every module it reaches partitioned by what would actually happen to it. Nothing is applied until
// ApplyTo is called, so the preview and the write are built from the same walk rather than from two.
public sealed record DependencyClosure(
    IReadOnlyList<ModuleId> Roots,
    DependencyClosureKind Kind,
    IReadOnlyList<ModuleId> Modules,
    IReadOnlyList<ModuleId> WillChange,
    IReadOnlyList<ModuleId> AlreadySettled,
    IReadOnlyList<ModuleId> RefusedOfficial,
    IReadOnlyList<ModuleId> RefusedNotInstalled,
    IReadOnlyList<ModuleId> MissingDependencies)
{
    private const int NamesShown = 12;

    public bool Enable => Kind == DependencyClosureKind.EnableWhatItNeeds;

    public bool ChangesAnything => WillChange.Count > 0;

    // Whether the closure would move anything other than the roots themselves. That is the whole
    // difference between this action and the plain Enable or Disable beside it on the menu, so it is
    // also the test for whether the item is worth offering at all.
    public bool ChangesAnythingBeyondRoots => WillChange.Any(id => !Roots.Contains(id));

    public static DependencyClosure For(ModuleEnvironment environment, ModuleId root, DependencyClosureKind kind) =>
        For(environment, [root], kind);

    // The union of the closures, walked once from every root together rather than once per root and
    // merged afterwards: a module two of the roots both need is reached once, and the preview the
    // user accepts is the same walk the write is built from.
    public static DependencyClosure For(
        ModuleEnvironment environment, IReadOnlyList<ModuleId> roots, DependencyClosureKind kind)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(roots);

        var byId = environment.ById;
        var enable = kind == DependencyClosureKind.EnableWhatItNeeds;

        var reached = new HashSet<ModuleId>(roots);
        var missing = new List<ModuleId>();
        var queue = new Queue<ModuleId>();

        foreach (var root in reached)
            queue.Enqueue(root);

        var dependents = enable ? null : DependentsOf(environment);

        // A visited set is what makes this terminate: the real graph has cycles (a library and a
        // patch that each declare the other), and an id already reached is never queued again.
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var next in enable ? Requirements(byId, current, missing) : dependents!.Next(current))
            {
                if (reached.Add(next))
                    queue.Enqueue(next);
            }
        }

        var willChange = new List<ModuleId>();
        var settled = new List<ModuleId>();
        var official = new List<ModuleId>();
        var notInstalled = new List<ModuleId>();
        var modules = new List<ModuleId>();

        // Walked in load order rather than in the order the graph happened to be traversed, so the
        // preview reads down the list the user is looking at.
        foreach (var entry in environment.Entries)
        {
            if (!reached.Contains(entry.Id))
                continue;

            modules.Add(entry.Id);

            if (entry.IsEnabled == enable)
                settled.Add(entry.Id);
            else if (entry.IsOfficial)
                official.Add(entry.Id);
            else if (enable && entry.IsOrphan)
                notInstalled.Add(entry.Id);
            else
                willChange.Add(entry.Id);
        }

        return new DependencyClosure(
            [.. roots.Distinct()], kind, modules, willChange, settled, official, notInstalled,
            enable ? [.. missing.Distinct()] : []);
    }

    // The whole closure is handed to WithEnabled, not just the part expected to move, so the guard
    // that refuses official modules is the thing actually enforcing it rather than this type's
    // bookkeeping agreeing with it by luck.
    public ModuleEnvironment ApplyTo(ModuleEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var scope = Modules.ToHashSet();

        return environment.WithEnabled(Enable, entry => scope.Contains(entry.Id));
    }

    public string Describe() => string.Join(Environment.NewLine, Lines(preview: true));

    public string DescribeOutcome() => string.Join(" ", Lines(preview: false));

    private IReadOnlyList<string> Lines(bool preview)
    {
        var lines = new List<string>();

        if (preview)
        {
            lines.Add(Roots.Count == 1
                ? Strings.Current.Format(
                    Enable ? "Core.LoadOrder.Closure.EnableHeader" : "Core.LoadOrder.Closure.DisableHeader",
                    Roots[0])
                : Strings.Current.Plural(
                    Enable ? "Core.LoadOrder.Closure.EnableHeader.Many" : "Core.LoadOrder.Closure.DisableHeader.Many",
                    Roots.Count, Names(Roots)));

            // What the closure pulled in beyond the rows the user actually picked. Without it a
            // multi-select closure reads as a list of the selection with a longer count on the end,
            // and the modules it reached on their behalf are the whole point of the action.
            var beyond = Modules.Where(id => !Roots.Contains(id)).ToList();

            if (beyond.Count > 0)
                lines.Add(Strings.Current.Plural("Core.LoadOrder.Closure.BeyondSelection", beyond.Count, Names(beyond)));
        }

        lines.Add((WillChange.Count, preview) switch
        {
            (0, true) => Strings.Current["Core.LoadOrder.Closure.NothingWouldChange"],
            (0, false) => Strings.Current["Core.LoadOrder.Closure.NothingChanged"],
            (_, true) => Strings.Current.Plural(
                Enable
                    ? "Core.LoadOrder.Closure.WillChangePreview.Enable"
                    : "Core.LoadOrder.Closure.WillChangePreview.Disable",
                WillChange.Count, Names(WillChange)),
            _ => Strings.Current.Plural(
                Enable
                    ? "Core.LoadOrder.Closure.WillChangeOutcome.Enable"
                    : "Core.LoadOrder.Closure.WillChangeOutcome.Disable",
                WillChange.Count, Names(WillChange))
        });

        if (AlreadySettled.Count > 0)
            lines.Add(Strings.Current.Plural(AlreadySettledKey(preview), AlreadySettled.Count));

        if (RefusedOfficial.Count > 0)
        {
            lines.Add(Strings.Current.Plural(
                "Core.LoadOrder.Closure.RefusedOfficial", RefusedOfficial.Count, Names(RefusedOfficial)));
        }

        if (RefusedNotInstalled.Count > 0)
        {
            lines.Add(Strings.Current.Plural(
                "Core.LoadOrder.Closure.RefusedNotInstalled", RefusedNotInstalled.Count, Names(RefusedNotInstalled)));
        }

        if (MissingDependencies.Count > 0)
        {
            lines.Add(Strings.Current.Plural(
                "Core.LoadOrder.Closure.MissingDependencies", MissingDependencies.Count, Names(MissingDependencies)));
        }

        return lines;
    }

    // One whole sentence per tense and state, rather than a sentence with a verb and an adjective
    // dropped into it: a fragment looked up on its own never sees the count or the noun it has to
    // agree with, which no catalog can repair in a language that inflects either.
    private string AlreadySettledKey(bool preview) => (Enable, preview) switch
    {
        (true, true) => "Core.LoadOrder.Closure.AlreadySettled.Enabled.Present",
        (true, false) => "Core.LoadOrder.Closure.AlreadySettled.Enabled.Past",
        (false, true) => "Core.LoadOrder.Closure.AlreadySettled.Disabled.Present",
        _ => "Core.LoadOrder.Closure.AlreadySettled.Disabled.Past"
    };

    private static string Names(IReadOnlyList<ModuleId> ids) =>
        ids.Count <= NamesShown
            ? string.Join(", ", ids.Select(id => id.Value))
            : Strings.Current.Format(
                "Core.LoadOrder.Closure.AndMore",
                string.Join(", ", ids.Take(NamesShown).Select(id => id.Value)), ids.Count - NamesShown);

    private static IEnumerable<ModuleId> Requirements(
        IReadOnlyDictionary<ModuleId, ModuleEntry> byId,
        ModuleId id,
        List<ModuleId> missing)
    {
        if (!byId.TryGetValue(id, out var entry))
            yield break;

        foreach (var dependency in entry.Dependencies)
        {
            if (!IsRequirement(dependency, entry.Id))
                continue;

            if (byId.ContainsKey(dependency.TargetId))
            {
                yield return dependency.TargetId;
                continue;
            }

            // BLSE provides these at runtime and they have no folder by design, so listing one as
            // missing would send the user looking for a mod that does not exist.
            if (!BlseFeatures.IsFeatureId(dependency.TargetId))
                missing.Add(dependency.TargetId);
        }
    }

    private static bool IsRequirement(ModuleDependency dependency, ModuleId declaringId) =>
        !dependency.IsIncompatible
        && !dependency.IsOptional
        && dependency.TargetId != declaringId;

    private static DependentIndex DependentsOf(ModuleEnvironment environment)
    {
        var index = new Dictionary<ModuleId, List<ModuleId>>();

        foreach (var entry in environment.Entries)
        {
            foreach (var dependency in entry.Dependencies)
            {
                if (!IsRequirement(dependency, entry.Id))
                    continue;

                if (!index.TryGetValue(dependency.TargetId, out var list))
                    index[dependency.TargetId] = list = [];

                list.Add(entry.Id);
            }
        }

        return new DependentIndex(index);
    }

    private sealed record DependentIndex(Dictionary<ModuleId, List<ModuleId>> Index)
    {
        public IEnumerable<ModuleId> Next(ModuleId id) =>
            Index.TryGetValue(id, out var dependents) ? dependents : [];
    }
}
