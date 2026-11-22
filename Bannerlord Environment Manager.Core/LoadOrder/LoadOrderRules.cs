using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// One order the player stated outright: Earlier loads somewhere before Later. Unlike a choice, which
// seats a module directly below another, a rule only fixes which of the two comes first, so every sort
// is still free to place both wherever the rest of the list allows.
public sealed record LoadOrderRule(ModuleId Earlier, ModuleId Later)
{
    public string Describe() => Strings.Current.Format("Core.LoadOrder.Rules.Describe", Earlier, Later);
}

// Why a rule cannot be added. Each is a different thing to tell the player.
public enum LoadOrderRuleRefusal
{
    SameModule,
    AlreadyStated,

    // The opposite order is already fixed, by what the mods declare or by the player's other rules.
    Contradicts
}

// A rule a sort could not keep, because the opposite order is now declared.
public sealed record UnkeptLoadOrderRule(LoadOrderRule Rule)
{
    public string Describe() => Strings.Current.Format("Core.LoadOrder.Rules.NotKept", Rule.Earlier, Rule.Later);
}

// Two modules the player says must not be on together. No order satisfies it, so a sort cannot fix it:
// it names the pair whenever both are on. Stored the same way round whichever module it was stated
// from, so the pair is one statement and not two.
public sealed record IncompatiblePair(ModuleId First, ModuleId Second)
{
    public static IncompatiblePair Of(ModuleId one, ModuleId other) =>
        string.Compare(one.Value, other.Value, StringComparison.OrdinalIgnoreCase) <= 0 ? new(one, other) : new(other, one);

    public bool Names(ModuleId id) => First == id || Second == id;

    public string Describe() => Strings.Current.Format("Core.LoadOrder.Rules.Incompatible.Describe", First, Second);

    public string DescribeOnTogether() => Strings.Current.Format("Core.LoadOrder.Rules.Incompatible.OnTogether", First, Second);
}

// The player's rules sit between manual ordering and a plain sort: they are constraints the sort
// treats exactly as it treats a declared order, so no sort key can break one. A declaration still
// outranks a rule, since a rule that contradicts one would be a loop no order satisfies; such a rule is
// refused when it is added and named when a later update to a mod turns it into one. A rule naming a
// module that is not installed right now is kept, for the same reason a pin is.
public sealed class LoadOrderRules
{
    private LoadOrderRules(IReadOnlyList<LoadOrderRule> items, IReadOnlyList<IncompatiblePair> incompatible)
    {
        Items = items;
        Incompatible = incompatible;
    }

    public static LoadOrderRules Empty { get; } = new([], []);

    // Insertion order, so the list the player sees does not reshuffle itself between sessions.
    public IReadOnlyList<LoadOrderRule> Items { get; }

    // The pairs the player marked as never on together, in the order they were stated.
    public IReadOnlyList<IncompatiblePair> Incompatible { get; }

    public bool IsEmpty => Items.Count == 0 && Incompatible.Count == 0;

    public static LoadOrderRules Of(IEnumerable<LoadOrderRule> items, IEnumerable<IncompatiblePair>? incompatible = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        var kept = items.Where(item => item.Earlier != item.Later).Distinct().ToList();

        var pairs = (incompatible ?? [])
            .Where(pair => pair.First != pair.Second)
            .Select(pair => IncompatiblePair.Of(pair.First, pair.Second))
            .Distinct()
            .ToList();

        return kept.Count == 0 && pairs.Count == 0 ? Empty : new LoadOrderRules(kept, pairs);
    }

    public bool IsIncompatible(ModuleId one, ModuleId other) => Incompatible.Contains(IncompatiblePair.Of(one, other));

    public LoadOrderRules WithIncompatible(ModuleId one, ModuleId other) =>
        Of(Items, [.. Incompatible, IncompatiblePair.Of(one, other)]);

    public LoadOrderRules WithoutIncompatible(IncompatiblePair pair) => Of(Items, Incompatible.Where(item => item != pair));

    public IReadOnlyList<IncompatiblePair> IncompatibleNaming(ModuleId id) => [.. Incompatible.Where(pair => pair.Names(id))];

    // The incompatible pairs with both modules on in this list. An order cannot answer one of these;
    // only turning one of the two off does, and which one is the player's call.
    public IReadOnlyList<IncompatiblePair> OnTogether(IReadOnlyList<ModuleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var on = entries.Where(entry => entry.IsEnabled).Select(entry => entry.Id).ToHashSet();

        return [.. Incompatible.Where(pair => on.Contains(pair.First) && on.Contains(pair.Second))];
    }

    public bool Contains(ModuleId earlier, ModuleId later) => Items.Contains(new LoadOrderRule(earlier, later));

    public LoadOrderRules With(ModuleId earlier, ModuleId later) => Of([.. Items, new LoadOrderRule(earlier, later)], Incompatible);

    public LoadOrderRules Without(LoadOrderRule rule) => Of(Items.Where(item => item != rule), Incompatible);

    // Every rule that names this module, on either side.
    public IReadOnlyList<LoadOrderRule> Naming(ModuleId id) => [.. Items.Where(item => item.Earlier == id || item.Later == id)];

    // Null when the rule can be added to this list of modules.
    public LoadOrderRuleRefusal? RefusalFor(IReadOnlyList<ModuleEntry> entries, ModuleId earlier, ModuleId later)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (earlier == later)
            return LoadOrderRuleRefusal.SameModule;

        if (Contains(earlier, later))
            return LoadOrderRuleRefusal.AlreadyStated;

        var sortable = Sortable(entries);
        var graph = ConstraintGraph.For(sortable, KeptAmong(sortable).Kept);
        var from = IndexOf(sortable, later);
        var to = IndexOf(sortable, earlier);

        // A rule naming a module that is not here cannot be checked against anything yet. The sort
        // checks it again the day both are present.
        return from >= 0 && to >= 0 && graph.Constrains(from, to) ? LoadOrderRuleRefusal.Contradicts : null;
    }

    public static string Describe(LoadOrderRuleRefusal refusal, ModuleId earlier, ModuleId later) => refusal switch
    {
        LoadOrderRuleRefusal.SameModule => Strings.Current["Core.LoadOrder.Rules.Refused.Same"],
        LoadOrderRuleRefusal.AlreadyStated => Strings.Current.Format("Core.LoadOrder.Rules.Refused.Already", earlier, later),
        _ => Strings.Current.Format("Core.LoadOrder.Rules.Refused.Contradicts", earlier, later)
    };

    // The rules a sort of these modules must honor, and the ones it cannot. Taken oldest first, so
    // when two of the player's rules have come to contradict each other the one stated first stands.
    public (IReadOnlyList<LoadOrderRule> Kept, IReadOnlyList<UnkeptLoadOrderRule> Unkept) KeptAmong(IReadOnlyList<ModuleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var kept = new List<LoadOrderRule>();
        var unkept = new List<UnkeptLoadOrderRule>();

        if (Items.Count == 0)
            return (kept, unkept);

        var sortable = Sortable(entries);
        var graph = ConstraintGraph.For(sortable);

        foreach (var rule in Items)
        {
            var earlier = IndexOf(sortable, rule.Earlier);
            var later = IndexOf(sortable, rule.Later);

            if (earlier < 0 || later < 0)
                continue;

            if (graph.Constrains(later, earlier))
            {
                unkept.Add(new UnkeptLoadOrderRule(rule));
                continue;
            }

            kept.Add(rule);
            graph = ConstraintGraph.For(sortable, kept);
        }

        return (kept, unkept);
    }

    // The rules this order breaks, each as the pair that is the wrong way round. A rule the
    // declarations contradict is left out: no order keeps it, and the sort names it on its own.
    public IReadOnlyList<ConstraintViolation> BrokenBy(IReadOnlyList<ModuleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var positions = new Dictionary<ModuleId, int>();

        for (var i = 0; i < entries.Count; i++)
        {
            if (!entries[i].IsOrphan)
                positions[entries[i].Id] = i;
        }

        return [.. KeptAmong(entries).Kept
            .Where(rule => positions[rule.Earlier] > positions[rule.Later])
            .Select(rule => new ConstraintViolation(rule.Earlier, rule.Later))];
    }

    // What the validator adds for the player's rules: an order arranged by hand that breaks one, and
    // an incompatible pair that is on together. A broken rule repairs by the smallest move that keeps
    // every rule; an incompatible pair has no repair BEM can choose, since either module could go.
    public IReadOnlyList<LoadOrderIssue> IssuesIn(ModuleEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var issues = new List<LoadOrderIssue>();

        foreach (var broken in BrokenBy(environment.Entries))
        {
            issues.Add(new LoadOrderIssue(
                IssueKind.OrderViolation,
                IssueSeverities.For(IssueKind.OrderViolation),
                broken.Later,
                broken.Earlier,
                Strings.Current.Format("Core.LoadOrder.Rules.Broken", broken.Earlier, broken.Later),
                Repair));
        }

        foreach (var pair in OnTogether(environment.Entries))
        {
            issues.Add(new LoadOrderIssue(
                IssueKind.Incompatible,
                IssueSeverities.For(IssueKind.Incompatible),
                pair.First,
                pair.Second,
                pair.DescribeOnTogether()));
        }

        return issues;
    }

    private ModuleEnvironment Repair(ModuleEnvironment environment)
    {
        var result = LoadOrderSorter.Sort(environment, ModuleSortPlan.Empty, null, null, this);

        return result.Failed ? environment : environment with { Entries = result.Entries };
    }

    // An orphan has no manifest and takes no part in a sort, so a rule naming one waits like a rule
    // naming a module that is not installed.
    private static List<ModuleEntry> Sortable(IReadOnlyList<ModuleEntry> entries) => [.. entries.Where(entry => !entry.IsOrphan)];

    private static int IndexOf(List<ModuleEntry> entries, ModuleId id) => entries.FindIndex(entry => entry.Id == id);
}

public sealed class LoadOrderRuleStore(string filePath)
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string IncompatibleKind = "Incompatible";

    // Kind is absent on a before and after rule, so a file written before incompatible pairs existed
    // reads as it always did.
    private sealed record Row(string Earlier, string Later, string? Kind = null);

    public string FilePath { get; } = filePath;

    // Per version, as pins and choices are: a rule describes one specific set of modules.
    public static string GetDefaultPath(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, "load-order-rules.json");

    // A file that will not parse throws, so a corrupt file cannot read as no rules and then be overwritten.
    public LoadOrderRules Read()
    {
        if (!File.Exists(FilePath))
            return LoadOrderRules.Empty;

        var rows = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(FilePath)) ?? [];

        var named = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.Earlier) && !string.IsNullOrWhiteSpace(row.Later))
            .ToList();

        return LoadOrderRules.Of(
            named.Where(row => row.Kind != IncompatibleKind)
                .Select(row => new LoadOrderRule(new ModuleId(row.Earlier), new ModuleId(row.Later))),
            named.Where(row => row.Kind == IncompatibleKind)
                .Select(row => IncompatiblePair.Of(new ModuleId(row.Earlier), new ModuleId(row.Later))));
    }

    public void Write(LoadOrderRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(
            rules.Items.Select(item => new Row(item.Earlier.Value, item.Later.Value))
                .Concat(rules.Incompatible.Select(pair => new Row(pair.First.Value, pair.Second.Value, IncompatibleKind)))
                .ToList(),
            Format));
    }
}
