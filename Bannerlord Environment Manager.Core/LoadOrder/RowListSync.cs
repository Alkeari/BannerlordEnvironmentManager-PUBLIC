using System.Collections.ObjectModel;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// Bringing a bound collection to a new sequence without emptying it first.
//
// Clearing an ObservableCollection and re-adding its contents raises one Reset plus one Add per item,
// and a list bound to it is genuinely empty in between: the Play tab flashed blank and threw the
// scroll position to the top on every rescan, over two hundred notifications for a list that had
// usually not changed at all. Only what actually differs is applied here, so an unchanged sequence
// raises nothing and one added module is one Add.
public static class RowListSync
{
    // The number of collection operations applied, which is zero when the two sequences already agree.
    public static int Apply<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> desired,
        IEqualityComparer<T>? comparer = null)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);

        comparer ??= EqualityComparer<T>.Default;

        var applied = 0;

        // Counted rather than merely named, so a target holding an item twice is trimmed to however
        // many copies the new sequence holds. Removing first keeps a deletion to one operation: left
        // in place, the stale row would be shuffled along by a move at every position after it.
        var wanted = new Dictionary<T, int>(comparer);

        foreach (var item in desired)
            wanted[item] = wanted.TryGetValue(item, out var seen) ? seen + 1 : 1;

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (wanted.TryGetValue(target[i], out var remaining) && remaining > 0)
            {
                wanted[target[i]] = remaining - 1;
                continue;
            }

            target.RemoveAt(i);
            applied++;
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && comparer.Equals(target[i], desired[i]))
                continue;

            var found = IndexOf(target, desired[i], i, comparer);

            if (found >= 0)
                target.Move(found, i);
            else
                target.Insert(i, desired[i]);

            applied++;
        }

        return applied;
    }

    private static int IndexOf<T>(ObservableCollection<T> items, T value, int from, IEqualityComparer<T> comparer)
        where T : notnull
    {
        for (var i = from; i < items.Count; i++)
        {
            if (comparer.Equals(items[i], value))
                return i;
        }

        return -1;
    }
}
