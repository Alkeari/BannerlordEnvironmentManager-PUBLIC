namespace BannerlordEnvironmentManager.Core.Diagnostics;

/// <summary>
/// Brings a bound list to a wanted sequence with the fewest edits. Clearing an ObservableCollection and
/// adding every row back raises one change notification per row, each of which the bound list realizes
/// on the UI thread; when nothing differs this raises none.
/// </summary>
public static class ListSync
{
    public static int Apply<T>(IList<T> target, IReadOnlyList<T> wanted) where T : class
    {
        var edits = 0;
        var keep = new HashSet<T>(wanted, ReferenceEqualityComparer.Instance as IEqualityComparer<T>);

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(target[i]))
            {
                target.RemoveAt(i);
                edits++;
            }
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], wanted[i]))
                continue;

            var at = -1;

            for (var j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], wanted[i]))
                {
                    at = j;
                    break;
                }
            }

            if (at >= 0)
                target.RemoveAt(at);

            target.Insert(i, wanted[i]);
            edits++;
        }

        return edits;
    }
}
