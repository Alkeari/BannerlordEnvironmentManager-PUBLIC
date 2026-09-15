using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

public enum ModuleSortKey
{
    Name,
    Id,
    Enabled,
    Tier
}

public sealed record ModuleSortLevel(ModuleSortKey Key, bool Descending = false);

// Which order the user wants among modules the constraints leave free. It never decides the load order
// on its own: LoadOrderSorter satisfies every declared constraint first and consults this only to
// choose between modules that are genuinely tied.
//
// Precedence is position in this list and is never stored on a level, so removing one renumbers the
// rest by construction rather than by a renumbering pass that could be forgotten.
public sealed class ModuleSortPlan
{
    public const int MaxLevels = 5;

    public static ModuleSortPlan Empty { get; } = new([]);

    // What Auto-Sort applies, and what a sort falls back to when no plan is named. Tier first, because
    // it puts the BUTR stack ahead of Native and the trailing patches last wherever nothing declared
    // says otherwise; id settles what the tier leaves level. The constraints are not a level here: they
    // are absolute and the topological pass keeps them whatever this plan asks for.
    public static ModuleSortPlan Default { get; } =
        Of(new ModuleSortLevel(ModuleSortKey.Tier), new ModuleSortLevel(ModuleSortKey.Id));

    private ModuleSortPlan(IReadOnlyList<ModuleSortLevel> levels) => Levels = levels;

    public IReadOnlyList<ModuleSortLevel> Levels { get; }

    public bool IsEmpty => Levels.Count == 0;

    public bool IsFull => Levels.Count >= MaxLevels;

    public static ModuleSortPlan Of(params ModuleSortLevel[] levels)
    {
        ArgumentNullException.ThrowIfNull(levels);

        if (levels.Length > MaxLevels)
            throw new ArgumentException($"A sort takes at most {MaxLevels} keys.", nameof(levels));

        if (levels.Select(level => level.Key).Distinct().Count() != levels.Length)
            throw new ArgumentException("A key can only appear once in a sort.", nameof(levels));

        return levels.Length == 0 ? Empty : new ModuleSortPlan([.. levels]);
    }

    public bool Contains(ModuleSortKey key) => Levels.Any(level => level.Key == key);

    public bool CanAdd(ModuleSortKey key) => Contains(key) || !IsFull;

    // 1 for the most important key, 0 for one that is not chosen at all.
    public int PrecedenceOf(ModuleSortKey key)
    {
        for (var i = 0; i < Levels.Count; i++)
        {
            if (Levels[i].Key == key)
                return i + 1;
        }

        return 0;
    }

    public bool DescendingFor(ModuleSortKey key) =>
        Levels.FirstOrDefault(level => level.Key == key)?.Descending ?? false;

    public ModuleSortPlan Toggle(ModuleSortKey key)
    {
        if (Contains(key))
            return Of([.. Levels.Where(level => level.Key != key)]);

        return IsFull ? this : Of([.. Levels, new ModuleSortLevel(key)]);
    }

    public ModuleSortPlan ToggleDirection(ModuleSortKey key) =>
        Contains(key)
            ? Of([.. Levels.Select(level => level.Key == key ? level with { Descending = !level.Descending } : level)])
            : this;

    public ModuleSortPlan Cleared() => Empty;

    public string Describe() =>
        IsEmpty
            ? Strings.Current["Core.LoadOrder.SortPlan.Empty"]
            : string.Join(Strings.Current["Core.LoadOrder.SortPlan.ThenSeparator"], Levels.Select(Describe));

    public static string Describe(ModuleSortKey key) => key switch
    {
        ModuleSortKey.Name => Strings.Current["Core.LoadOrder.SortKey.Name"],
        ModuleSortKey.Enabled => Strings.Current["Core.LoadOrder.SortKey.Enabled"],
        ModuleSortKey.Tier => Strings.Current["Core.LoadOrder.SortKey.Tier"],
        _ => Strings.Current["Core.LoadOrder.SortKey.Id"]
    };

    private static string Describe(ModuleSortLevel level) =>
        level.Descending
            ? Strings.Current.Format("Core.LoadOrder.SortPlan.Descending", Describe(level.Key))
            : Describe(level.Key);
}
