namespace BannerlordEnvironmentManager.Core.Modules;

// A record's synthesized Equals compares a member through EqualityComparer<T>.Default, which for a
// member typed as IReadOnlyList<T> is reference equality. Two reads of the same SubModule.xml build
// separate lists, so an identical manifest never equalled itself and everything that asks "has this
// changed" was told yes on every scan: that is what tore down and refilled the whole Play list every
// time a build tool touched the Modules folder.
internal static class ValueList
{
    public static bool Equal<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left is null || right is null || left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[i], right[i]))
                return false;
        }

        return true;
    }

    public static int HashOf<T>(IReadOnlyList<T>? items)
    {
        if (items is null)
            return 0;

        var hash = new HashCode();
        hash.Add(items.Count);

        for (var i = 0; i < items.Count; i++)
            hash.Add(items[i]);

        return hash.ToHashCode();
    }
}
