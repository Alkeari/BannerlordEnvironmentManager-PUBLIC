using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

public enum LoadOrderChangeKind
{
    Added,
    Removed,
    Moved,
    Toggled
}

public sealed record LoadOrderChange(string Id, LoadOrderChangeKind Kind);

public sealed record LoadOrderDifference(IReadOnlyList<LoadOrderChange> Changes)
{
    public int AddedCount => Count(LoadOrderChangeKind.Added);

    public int RemovedCount => Count(LoadOrderChangeKind.Removed);

    public int MovedCount => Count(LoadOrderChangeKind.Moved);

    public int ToggledCount => Count(LoadOrderChangeKind.Toggled);

    public bool IsIdentical => Changes.Count == 0;

    public string Summary
    {
        get
        {
            var parts = new List<string>();

            if (AddedCount > 0)
                parts.Add(Strings.Current.Format("Core.LoadOrder.Comparer.Added", AddedCount));

            if (RemovedCount > 0)
                parts.Add(Strings.Current.Format("Core.LoadOrder.Comparer.Removed", RemovedCount));

            if (MovedCount > 0)
                parts.Add(Strings.Current.Format("Core.LoadOrder.Comparer.Moved", MovedCount));

            if (ToggledCount > 0)
                parts.Add(Strings.Current.Format("Core.LoadOrder.Comparer.Toggled", ToggledCount));

            return parts.Count == 0 ? Strings.Current["Core.LoadOrder.Comparer.Identical"] : string.Join(", ", parts);
        }
    }

    private int Count(LoadOrderChangeKind kind) => Changes.Count(c => c.Kind == kind);
}

public static class LoadOrderComparer
{
    // Added and Removed are stated from the target's point of view, so Compare(current, profile)
    // answers "what would restoring this profile do", which is the only question the UI asks.
    public static LoadOrderDifference Compare(LoadOrderSnapshot from, LoadOrderSnapshot to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var sourceEntries = Deduplicate(from);
        var targetEntries = Deduplicate(to);

        var source = sourceEntries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var target = targetEntries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        var changes = new List<LoadOrderChange>();

        foreach (var entry in targetEntries)
        {
            if (!source.ContainsKey(entry.Id))
                changes.Add(new LoadOrderChange(entry.Id, LoadOrderChangeKind.Added));
        }

        foreach (var entry in sourceEntries)
        {
            if (!target.ContainsKey(entry.Id))
                changes.Add(new LoadOrderChange(entry.Id, LoadOrderChangeKind.Removed));
        }

        var sourceCommon = sourceEntries.Where(e => target.ContainsKey(e.Id)).ToList();
        var targetCommon = targetEntries.Where(e => source.ContainsKey(e.Id)).ToList();

        // Comparing raw positions would call every module below an insertion "moved". The modules that
        // are not in the longest common subsequence are the ones a person would actually point at.
        var settled = LongestCommonSubsequence(
            [.. sourceCommon.Select(e => e.Id)],
            [.. targetCommon.Select(e => e.Id)]);

        foreach (var entry in targetCommon)
        {
            if (!settled.Contains(entry.Id))
                changes.Add(new LoadOrderChange(entry.Id, LoadOrderChangeKind.Moved));
        }

        foreach (var entry in targetCommon)
        {
            if (source[entry.Id].IsEnabled != entry.IsEnabled)
                changes.Add(new LoadOrderChange(entry.Id, LoadOrderChangeKind.Toggled));
        }

        return new LoadOrderDifference(changes);
    }

    private static List<LoadOrderSnapshotEntry> Deduplicate(LoadOrderSnapshot snapshot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return [.. snapshot.Entries.Where(e => seen.Add(e.Id))];
    }

    private static HashSet<string> LongestCommonSubsequence(string[] left, string[] right)
    {
        var lengths = new int[left.Length + 1, right.Length + 1];

        for (var i = left.Length - 1; i >= 0; i--)
        {
            for (var j = right.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(left[i], right[j], StringComparison.OrdinalIgnoreCase)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var common = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var x = 0;
        var y = 0;

        while (x < left.Length && y < right.Length)
        {
            if (string.Equals(left[x], right[y], StringComparison.OrdinalIgnoreCase))
            {
                common.Add(right[y]);
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return common;
    }
}
