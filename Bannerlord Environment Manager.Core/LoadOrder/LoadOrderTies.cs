using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// Two modules are tied when neither transitively constrains the other, so a sort key only ever has
// something to do where a tie exists. That is a property of the constraint graph, never of value
// equality on a key: two modules can hold the same name and still not be free to swap.
public static class LoadOrderTies
{
    public static int TiedModuleCount(IReadOnlyList<ModuleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return ConstraintGraph.For(entries).TiedModuleCount();
    }

    // Which chosen keys can never move a module, because no pair that is free to swap reaches them.
    // Aligned with the plan's levels, empty where the key does get a say.
    public static IReadOnlyList<string> UnreachableNotes(IReadOnlyList<ModuleEntry> entries, ModuleSortPlan plan)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(plan);

        var notes = new string[plan.Levels.Count];

        Array.Fill(notes, string.Empty);

        if (entries.Count < 2 || plan.IsEmpty)
            return notes;

        var tied = ConstraintGraph.For(entries).TiedPairs().ToList();
        var tiers = ModuleTierMap.For(entries);

        for (var level = 0; level < plan.Levels.Count; level++)
        {
            var above = plan.Levels.Take(level).Select(l => l.Key).ToList();

            if (tied.Any(pair => above.All(key => Agrees(entries, tiers, key, pair.A, pair.B))))
                continue;

            notes[level] = Strings.Current[level == 0
                ? "Core.LoadOrder.Ties.NeverAppliesTop"
                : "Core.LoadOrder.Ties.NeverAppliesBelow"];
        }

        return notes;
    }

    private static bool Agrees(IReadOnlyList<ModuleEntry> entries, ModuleTier[] tiers, ModuleSortKey key, int a, int b) => key switch
    {
        ModuleSortKey.Name => string.Equals(entries[a].DisplayName, entries[b].DisplayName, StringComparison.OrdinalIgnoreCase),
        ModuleSortKey.Enabled => entries[a].IsEnabled == entries[b].IsEnabled,
        ModuleSortKey.Tier => tiers[a] == tiers[b],
        _ => string.Equals(entries[a].Id.Value, entries[b].Id.Value, StringComparison.OrdinalIgnoreCase)
    };
}
