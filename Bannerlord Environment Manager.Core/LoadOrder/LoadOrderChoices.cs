using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// One order the player chose to settle a finding: Mover loads immediately after Anchor.
public sealed record LoadOrderChoice(ModuleId Mover, ModuleId Anchor);

// A choice a sort could not keep, because a declared order outranks it.
public sealed record UnkeptLoadOrderChoice(LoadOrderChoice Choice, ConstraintViolation Broken)
{
    public string Describe() => Strings.Current.Format(
        "Core.LoadOrder.Choices.NotKept", Choice.Mover, Choice.Anchor, Broken.Describe());
}

// What the player chose outranks the sort's own preferences, because nothing but a preference was
// separating those modules. It never outranks a declaration, and a choice that lost says so. A choice
// for a module that is not installed right now is kept, for the same reason a pin is.
public sealed class LoadOrderChoices
{
    private LoadOrderChoices(IReadOnlyList<LoadOrderChoice> items) => Items = items;

    public static LoadOrderChoices Empty { get; } = new([]);

    public IReadOnlyList<LoadOrderChoice> Items { get; }

    public static LoadOrderChoices Of(IEnumerable<LoadOrderChoice> items) => new([.. items.Distinct()]);

    // The newest choice wins, so choosing the other way round replaces the old one.
    public LoadOrderChoices With(ModuleId mover, ModuleId anchor) =>
        Of([.. Items.Where(item => !(item.Mover == anchor && item.Anchor == mover)), new LoadOrderChoice(mover, anchor)]);

    // Whether the player chose this module to load after the other, directly or through a chain of choices.
    // Taking a choice back removes it (WithoutContradictedBy), so a true answer is still what he wants.
    public bool ChoseAfter(ModuleId later, ModuleId earlier)
    {
        var seen = new HashSet<ModuleId> { later };
        var open = new Stack<ModuleId>(seen);

        while (open.TryPop(out var current))
        {
            foreach (var item in Items.Where(item => item.Mover == current))
            {
                if (item.Anchor == earlier)
                    return true;

                if (seen.Add(item.Anchor))
                    open.Push(item.Anchor);
            }
        }

        return false;
    }

    // An order that now has the mover above its anchor means the player took the choice back, by Undo or
    // by moving the module, so the next sort must not restore it.
    public LoadOrderChoices WithoutContradictedBy(IReadOnlyList<ModuleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var positions = Positions(entries);

        return Of(Items.Where(item =>
            !positions.TryGetValue(item.Mover, out var mover)
            || !positions.TryGetValue(item.Anchor, out var anchor)
            || mover > anchor));
    }

    // Returns the modules it placed and the choices it could not keep. Movers sharing an anchor, or
    // chained through one another, sit together directly below it, and every move is checked against the
    // declarations before it stands.
    internal (Dictionary<ModuleId, ModuleId> Placed, List<UnkeptLoadOrderChoice> Unkept) ApplyTo(
        List<ModuleEntry> order,
        IReadOnlyList<ModuleId> unorderable,
        IReadOnlyList<LoadOrderRule>? rules = null)
    {
        var placed = new Dictionary<ModuleId, ModuleId>();
        var unkept = new List<UnkeptLoadOrderChoice>();

        if (Items.Count == 0)
            return (placed, unkept);

        var movers = Items.Select(item => item.Mover).ToHashSet();
        var refused = new HashSet<LoadOrderChoice>();
        var baseline = LoadOrderSorter.Verify(order, unorderable, rules).Count;

        for (var pass = 0; pass <= Items.Count; pass++)
        {
            var changed = false;

            foreach (var group in Items.GroupBy(item => item.Mover))
            {
                var positions = Positions(order);

                if (!positions.TryGetValue(group.Key, out var from))
                    continue;

                // Below every anchor it was chosen to follow.
                var choice = group
                    .Where(item => item.Anchor != item.Mover && positions.ContainsKey(item.Anchor) && !refused.Contains(item))
                    .OrderByDescending(item => positions[item.Anchor])
                    .FirstOrDefault();

                if (choice is null)
                    continue;

                var anchorAt = positions[choice.Anchor];

                placed[group.Key] = choice.Anchor;

                if (from > anchorAt && Enumerable.Range(anchorAt + 1, from - anchorAt - 1).All(i => movers.Contains(order[i].Id)))
                    continue;

                // The same carrying the Health move does: a mod that must load after the mover goes with it,
                // or the choice that move recorded would be refused by the very next sort. Only the anchor
                // cannot be carried, since that is a declaration the choice really does break.
                var carried = new HashSet<ModuleId> { group.Key };
                List<ModuleEntry> candidate;
                IReadOnlyList<ConstraintViolation> broken;

                while (true)
                {
                    candidate = MovedBelow(order, carried, choice.Anchor);
                    broken = LoadOrderSorter.Verify(candidate, unorderable, rules);

                    if (broken.Count <= baseline)
                        break;

                    var toCarry = broken.Where(v => carried.Contains(v.Earlier)).Select(v => v.Later).ToList();

                    if (toCarry.Count == 0 || toCarry.Contains(choice.Anchor) || !toCarry.Any(carried.Add))
                        break;
                }

                if (broken.Count > baseline)
                {
                    refused.Add(choice);
                    placed.Remove(group.Key);
                    unkept.Add(new UnkeptLoadOrderChoice(choice, broken[0]));
                    changed = true;
                    continue;
                }

                order.Clear();
                order.AddRange(candidate);
                changed = true;
            }

            if (!changed)
                break;
        }

        return (placed, unkept);
    }

    private static List<ModuleEntry> MovedBelow(List<ModuleEntry> order, HashSet<ModuleId> moved, ModuleId anchor)
    {
        var result = order.Where(entry => !moved.Contains(entry.Id)).ToList();

        result.InsertRange(result.FindIndex(entry => entry.Id == anchor) + 1, order.Where(entry => moved.Contains(entry.Id)));

        return result;
    }

    private static Dictionary<ModuleId, int> Positions(IReadOnlyList<ModuleEntry> entries)
    {
        var positions = new Dictionary<ModuleId, int>();

        for (var i = 0; i < entries.Count; i++)
            positions[entries[i].Id] = i;

        return positions;
    }
}

public sealed class LoadOrderChoiceStore(string filePath)
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private sealed record Row(string Mover, string Anchor);

    public static string GetDefaultPath(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, "load-order-choices.json");

    // A file that will not parse throws, so a corrupt file cannot read as no choices and then be overwritten.
    public LoadOrderChoices Read()
    {
        if (!File.Exists(filePath))
            return LoadOrderChoices.Empty;

        var rows = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(filePath)) ?? [];

        return LoadOrderChoices.Of(rows
            .Where(row => !string.IsNullOrWhiteSpace(row.Mover) && !string.IsNullOrWhiteSpace(row.Anchor))
            .Select(row => new LoadOrderChoice(new ModuleId(row.Mover), new ModuleId(row.Anchor))));
    }

    public void Write(LoadOrderChoices choices)
    {
        ArgumentNullException.ThrowIfNull(choices);

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, JsonSerializer.Serialize(
            choices.Items.Select(item => new Row(item.Mover.Value, item.Anchor.Value)).ToList(), Format));
    }
}
