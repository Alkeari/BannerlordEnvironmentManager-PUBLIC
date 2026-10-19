using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// Earlier must sit closer to position 1 than Later and did not.
public sealed record ConstraintViolation(ModuleId Earlier, ModuleId Later)
{
    public string Describe() => Strings.Current.Format("Core.LoadOrder.Sorter.ConstraintViolation", Earlier, Later);
}

public sealed record SortResult(
    IReadOnlyList<ModuleEntry> Entries,
    IReadOnlyList<IReadOnlyList<ModuleId>> Cycles,
    IReadOnlyList<ConstraintViolation> Violations = null!,
    IReadOnlyList<UnhonoredPin> UnhonoredPins = null!,
    IReadOnlyList<ModuleSortReason> Reasons = null!)
{
    public IReadOnlyList<ConstraintViolation> Violations { get; init; } = Violations ?? [];

    // The pins this sort could not keep, each with the declared edge that outranked it. Empty is the
    // normal answer; a pin that lost is never dropped without being named.
    public IReadOnlyList<UnhonoredPin> UnhonoredPins { get; init; } = UnhonoredPins ?? [];

    // Why every module the sort placed is where it is, in the order they were placed. Modules caught in
    // a dependency loop are left out: the sort could not order them, so it has nothing to say about
    // where they went, and the loop is reported on its own.
    public IReadOnlyList<ModuleSortReason> Reasons { get; init; } = Reasons ?? [];

    public int MovedCount => Reasons.Count(reason => reason.Moved);

    // A sort that reports violations has failed its own invariant. Entries is the order that went in,
    // untouched, so a caller can hand it straight back rather than write an order proven invalid.
    public bool Failed => Violations.Count > 0;
}

// Every sort is a topological sort. A load order is a linearisation of the declared constraint graph,
// and any linearisation that satisfies every constraint is equally valid, so the plan never overrides a
// constraint: it only picks which of the modules that are ready right now goes next. That is why a sort
// here cannot produce an ordering violation whatever the user chose to sort by.
//
// Constraints are absolute and the plan, tier included, is a preference. That is not a contradiction
// with the tier leading the default plan: the tier decides wherever modules are free, which on a real
// install is nearly everywhere, and a declaration wins wherever one exists.
//
// Direction, because it is the one thing easy to encode backwards: a module that depends on another
// loads AFTER it, so the dependency sits at the LOWER index, closer to position 1. Every edge here runs
// from the module that must come first to the module that must come after it.
//
// The compliance floor is not left to the algorithm being right. Verify walks the finished list and
// checks every declared edge against it, and a sort that fails that check returns the order it was
// given rather than the one it built. That is what makes the lock structural: a sort key added later
// cannot break it quietly, because a broken order never leaves this method.
public static class LoadOrderSorter
{
    public static SortResult Sort(ModuleEnvironment environment, ModuleSortPlan? plan = null, ModulePins? pins = null)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var sortable = new List<ModuleEntry>(environment.Entries.Count);
        var orphans = new List<(int Index, ModuleEntry Entry)>();

        for (var index = 0; index < environment.Entries.Count; index++)
        {
            var entry = environment.Entries[index];

            if (entry.IsOrphan)
                orphans.Add((index, entry));
            else
                sortable.Add(entry);
        }

        var graph = ConstraintGraph.For(sortable);
        var inDegree = graph.InDegrees();

        var order = new ReadyOrder(sortable, ModuleTierMap.For(sortable), plan ?? ModuleSortPlan.Default);
        var ready = new PriorityQueue<int, int>(order);

        // A pinned module's slot is the index it already has, because the sortable list keeps the load
        // order's own order. That is what makes a pin need no stored index: "stay where you are" is
        // read off the list every time rather than remembered from the day it was pinned.
        var pinned = new bool[sortable.Count];

        if (pins is { IsEmpty: false })
        {
            for (var i = 0; i < sortable.Count; i++)
                pinned[i] = pins.Contains(sortable[i].Id);
        }

        var readyPinned = new SortedSet<int>();
        var lostPins = new List<(int Index, ModuleId? Earlier, ModuleId? Later)>();
        var placements = new List<(int Index, SortReasonKind Kind, ModuleId? Other, ModuleSortKey? Key)>(sortable.Count);
        var sorted = new List<ModuleEntry>(sortable.Count);
        var emitted = new bool[sortable.Count];

        // What made each module placeable, and when. Together they are the difference between "a
        // declaration decided this" and "the keys decided this", which are different claims.
        var satisfiedBy = new int[sortable.Count];
        var freeFrom = new int[sortable.Count];

        Array.Fill(satisfiedBy, -1);

        void Offer(int index)
        {
            if (pinned[index])
                readyPinned.Add(index);
            else
                ready.Enqueue(index, index);
        }

        ModuleId? FirstUnemittedSuccessor(int index)
        {
            foreach (var successor in graph.SuccessorsOf(index))
            {
                if (!emitted[successor])
                    return sortable[successor].Id;
            }

            return null;
        }

        ModuleId? FirstUnemittedPredecessor(int index)
        {
            foreach (var predecessor in graph.PredecessorsOf(index))
            {
                if (!emitted[predecessor])
                    return sortable[predecessor].Id;
            }

            return null;
        }

        // Only ever hands back a module whose declared predecessors have all been placed, pinned or
        // not, which is what keeps compliance above the pin rather than beside it.
        bool TryTake(out int index, out SortReasonKind kind, out ModuleId? other, out ModuleSortKey? key)
        {
            kind = SortReasonKind.Unopposed;
            other = null;
            key = null;

            if (ready.TryDequeue(out index, out _))
            {
                // The runner-up is whatever the queue would have handed back instead, so naming it and
                // the key that separated them is a claim the sort can actually support.
                if (ready.TryPeek(out var runnerUp, out _))
                {
                    kind = SortReasonKind.SortKey;
                    other = sortable[runnerUp].Id;
                    key = order.DecidingKey(index, runnerUp);
                }

                return true;
            }

            if (readyPinned.Count == 0)
                return false;

            // Nothing free is left, so a pinned module has to come early. Preferring one that a waiting
            // module actually declares it must load after means the reason given is the real constraint.
            var chosen = -1;
            ModuleId? blocked = null;

            foreach (var candidate in readyPinned)
            {
                if (FirstUnemittedSuccessor(candidate) is not { } successor)
                    continue;

                chosen = candidate;
                blocked = successor;
                break;
            }

            if (chosen < 0)
                chosen = readyPinned.Min;

            readyPinned.Remove(chosen);
            pinned[chosen] = false;

            lostPins.Add(blocked is null
                ? (chosen, null, null)
                : (chosen, sortable[chosen].Id, blocked));

            index = chosen;

            return true;
        }

        for (var i = 0; i < sortable.Count; i++)
        {
            if (inDegree[i] == 0)
                Offer(i);
        }

        while (sorted.Count < sortable.Count)
        {
            var step = sorted.Count;
            int current;
            SortReasonKind kind;
            ModuleId? other;
            ModuleSortKey? key;

            if (pinned[step] && readyPinned.Remove(step))
            {
                current = step;
                kind = SortReasonKind.Pinned;
                other = null;
                key = null;
            }
            else
            {
                if (pinned[step])
                {
                    // Its slot has come round and something it declares has not been placed yet. The
                    // pin loses, the constraint does not, and the pin is recorded rather than dropped.
                    lostPins.Add((step, FirstUnemittedPredecessor(step), sortable[step].Id));
                    pinned[step] = false;
                }

                if (!TryTake(out current, out kind, out other, out key))
                    break;

                // Placed the moment the last thing it must load after had been placed, so the
                // declaration is what decided it could go no earlier. That outranks the runner-up as an
                // explanation, and unlike the runner-up it is true whatever else was free.
                if (satisfiedBy[current] >= 0 && freeFrom[current] == step)
                {
                    kind = SortReasonKind.Constraint;
                    other = sortable[satisfiedBy[current]].Id;
                    key = null;
                }
            }

            placements.Add((current, kind, other, key));
            sorted.Add(sortable[current]);
            emitted[current] = true;

            foreach (var successor in graph.SuccessorsOf(current))
            {
                if (--inDegree[successor] == 0)
                {
                    satisfiedBy[successor] = current;
                    freeFrom[successor] = step + 1;
                    Offer(successor);
                }
            }
        }

        var cycles = new List<IReadOnlyList<ModuleId>>();

        if (sorted.Count != sortable.Count)
        {
            var remaining = new List<int>();

            for (var i = 0; i < sortable.Count; i++)
            {
                if (!emitted[i])
                    remaining.Add(i);
            }

            // What is left is not one loop. It is the loops, plus every module downstream of one, which
            // is only stuck because something it declares is. Those are separated here so a module that
            // merely depends on a loop is never named as part of it, and so that its own declarations
            // are still satisfied: the components come back in reverse topological order, so walking
            // them backwards places each one after everything it must load after.
            var components = graph.ComponentsOf(remaining);

            for (var i = components.Count - 1; i >= 0; i--)
            {
                var component = components[i];

                if (component.Count > 1)
                    cycles.Add([.. component.Select(index => sortable[index].Id)]);

                foreach (var index in component)
                {
                    sorted.Add(sortable[index]);
                    emitted[index] = true;
                }
            }
        }

        // An orphan has no manifest and so no constraint of its own; its recorded slot is the only
        // record of where the mod used to sit, so it is put back rather than pushed to the end.
        foreach (var (index, entry) in orphans)
            sorted.Insert(Math.Min(index, sorted.Count), entry);

        var violations = Verify(sorted, [.. cycles.SelectMany(cycle => cycle)]);

        // A sort that failed its own check returns the order it was given, so nothing moved and no pin
        // was overridden. Reporting one here would name a consequence that did not happen.
        return violations.Count == 0
            ? new SortResult(
                sorted,
                cycles,
                [],
                Overridden(lostPins, sortable, environment.Entries, sorted),
                Explain(placements, sortable, environment.Entries, sorted))
            : new SortResult(environment.Entries, cycles, violations);
    }

    private static IReadOnlyList<ModuleSortReason> Explain(
        List<(int Index, SortReasonKind Kind, ModuleId? Other, ModuleSortKey? Key)> placements,
        IReadOnlyList<ModuleEntry> sortable,
        IReadOnlyList<ModuleEntry> before,
        IReadOnlyList<ModuleEntry> after)
    {
        var was = PositionsOf(before);
        var now = PositionsOf(after);
        var reasons = new List<ModuleSortReason>(placements.Count);

        foreach (var (index, kind, other, key) in placements)
        {
            var id = sortable[index].Id;

            reasons.Add(new ModuleSortReason(
                id, was.GetValueOrDefault(id, -1), now.GetValueOrDefault(id, -1), kind, other, key));
        }

        return reasons;
    }

    private static IReadOnlyList<UnhonoredPin> Overridden(
        List<(int Index, ModuleId? Earlier, ModuleId? Later)> lostPins,
        IReadOnlyList<ModuleEntry> sortable,
        IReadOnlyList<ModuleEntry> before,
        IReadOnlyList<ModuleEntry> after)
    {
        if (lostPins.Count == 0)
            return [];

        var was = PositionsOf(before);
        var now = PositionsOf(after);

        var overridden = new List<UnhonoredPin>(lostPins.Count);

        foreach (var (index, earlier, later) in lostPins)
        {
            var id = sortable[index].Id;

            overridden.Add(new UnhonoredPin(
                id, was.GetValueOrDefault(id, -1), now.GetValueOrDefault(id, -1), earlier, later));
        }

        return [.. overridden.OrderBy(pin => pin.PinnedPosition)];
    }

    private static Dictionary<ModuleId, int> PositionsOf(IReadOnlyList<ModuleEntry> entries)
    {
        var positions = new Dictionary<ModuleId, int>();

        for (var i = 0; i < entries.Count; i++)
            positions[entries[i].Id] = i;

        return positions;
    }

    // Every declared edge, checked against the list that is about to be returned. Modules caught in a
    // cycle are left out: no order of a loop satisfies it, which is why the loop is reported separately
    // rather than counted here as a failure of the sort.
    public static IReadOnlyList<ConstraintViolation> Verify(IReadOnlyList<ModuleEntry> entries, IReadOnlyList<ModuleId> unorderable)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(unorderable);

        var positions = new Dictionary<ModuleId, int>();

        // An orphan is left out on both sides, exactly as the graph leaves it out: it has no manifest,
        // so it declares nothing and nothing about it can be satisfied or broken.
        for (var i = 0; i < entries.Count; i++)
        {
            if (!entries[i].IsOrphan)
                positions[entries[i].Id] = i;
        }

        var looped = unorderable.ToHashSet();
        var violations = new List<ConstraintViolation>();

        foreach (var entry in entries)
        {
            if (entry.IsOrphan || looped.Contains(entry.Id))
                continue;

            foreach (var dependency in entry.Dependencies)
            {
                if (dependency.IsIncompatible || dependency.Order == DependencyOrder.None)
                    continue;

                if (dependency.TargetId == entry.Id || looped.Contains(dependency.TargetId))
                    continue;

                if (!positions.TryGetValue(dependency.TargetId, out var there))
                    continue;

                var here = positions[entry.Id];

                var (earlier, later) = dependency.Order == DependencyOrder.LoadBeforeThis
                    ? (dependency.TargetId, entry.Id)
                    : (entry.Id, dependency.TargetId);

                var satisfied = dependency.Order == DependencyOrder.LoadBeforeThis
                    ? there < here
                    : there > here;

                if (!satisfied)
                    violations.Add(new ConstraintViolation(earlier, later));
            }
        }

        return violations;
    }

    // Ranks the modules that are ready at this step. The final fall-back is the position the module
    // came in at, which is what makes an empty plan leave tied modules exactly where they were.
    private sealed class ReadyOrder(IReadOnlyList<ModuleEntry> entries, ModuleTier[] tiers, ModuleSortPlan plan) : IComparer<int>
    {
        public int Compare(int a, int b)
        {
            foreach (var level in plan.Levels)
            {
                var result = Compare(level, a, b);

                if (result != 0)
                    return result;
            }

            return a.CompareTo(b);
        }

        // Which key separated these two, so a sort can name the one that actually decided rather than
        // the first one in the plan. Null when every key tied and the incoming order broke it.
        public ModuleSortKey? DecidingKey(int a, int b)
        {
            foreach (var level in plan.Levels)
            {
                if (Compare(level, a, b) != 0)
                    return level.Key;
            }

            return null;
        }

        private int Compare(ModuleSortLevel level, int a, int b)
        {
            var result = level.Key switch
            {
                ModuleSortKey.Name => string.Compare(entries[a].DisplayName, entries[b].DisplayName, StringComparison.OrdinalIgnoreCase),
                ModuleSortKey.Enabled => EnabledRank(entries[a]).CompareTo(EnabledRank(entries[b])),
                ModuleSortKey.Tier => tiers[a].CompareTo(tiers[b]),
                _ => string.Compare(entries[a].Id.Value, entries[b].Id.Value, StringComparison.OrdinalIgnoreCase)
            };

            return level.Descending ? -result : result;
        }

        private static int EnabledRank(ModuleEntry entry) => entry.IsEnabled ? 0 : 1;
    }
}
