namespace BannerlordEnvironmentManager.Core.Diagnostics;

// One action several selected findings share: every row it can apply to, and each row's own instance
// of it. A row with no such action is simply not in the group, so it is skipped when the action runs.
public sealed record BatchActionGroup<TRow, TAction>(string Key, IReadOnlyList<TRow> Rows, IReadOnlyList<TAction> Actions);

public static class BatchActionGroups
{
    // Groups by what the action does. A null key marks an action with no batch form (opening a folder).
    // The action most rows share comes first; ties keep the order they were met in.
    public static IReadOnlyList<BatchActionGroup<TRow, TAction>> Group<TRow, TAction>(
        IEnumerable<TRow> rows,
        Func<TRow, IEnumerable<TAction>> actionsOf,
        Func<TAction, string?> keyOf)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(actionsOf);
        ArgumentNullException.ThrowIfNull(keyOf);

        var order = new List<string>();
        var groupRows = new Dictionary<string, List<TRow>>(StringComparer.Ordinal);
        var groupActions = new Dictionary<string, List<TAction>>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var action in actionsOf(row))
            {
                if (keyOf(action) is not { } key)
                    continue;

                if (!groupRows.ContainsKey(key))
                {
                    order.Add(key);
                    groupRows[key] = [];
                    groupActions[key] = [];
                }

                groupActions[key].Add(action);

                if (seen.Add(key))
                    groupRows[key].Add(row);
            }
        }

        return order
            .OrderByDescending(key => groupRows[key].Count)
            .Select(key => new BatchActionGroup<TRow, TAction>(key, groupRows[key], groupActions[key]))
            .ToList();
    }
}
