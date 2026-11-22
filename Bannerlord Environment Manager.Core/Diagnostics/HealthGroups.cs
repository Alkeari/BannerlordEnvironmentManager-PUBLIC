namespace BannerlordEnvironmentManager.Core.Diagnostics;

// Which Health rows one press acts on. The page and the button both use this, so a group action
// cannot reach another kind and the accept button cannot name a count it does not accept.
public static class HealthGroups
{
    public static string Key(string family, string detail) => family + "|" + detail;

    public static IReadOnlyList<T> Matching<T>(
        IEnumerable<T> rows, string key, Func<T, string> keyOf, Func<T, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(keyOf);

        return rows.Where(row =>
            (include is null || include(row)) && string.Equals(keyOf(row), key, StringComparison.Ordinal)).ToList();
    }

    // Every selected row that can still be accepted. A blank headline is not a reason to leave one out.
    public static IReadOnlyList<T> Acceptable<T>(
        IEnumerable<T> selected, Func<T, bool> canAccept, Func<T, bool> isAccepted)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(canAccept);
        ArgumentNullException.ThrowIfNull(isAccepted);

        return selected.Where(row => canAccept(row) && !isAccepted(row)).ToList();
    }
}
