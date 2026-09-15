namespace BannerlordEnvironmentManager.Core.LoadOrder;

// The Windows Explorer selection gestures, as arithmetic over the rows a list is showing. Which rows
// a range covers, what a toggle does to a set, and where the anchor a later range extends from ends
// up are all decided here; routing a keystroke or a click to one of these is the view's half and
// cannot be tested without a window.
public enum ListSelectionGesture
{
    // A plain click, or the list moving its own selection under an arrow key.
    Replace,

    // Ctrl+click.
    Toggle,

    // Shift+click.
    Extend,

    // Ctrl+Shift+click.
    ExtendAdd,

    // Ctrl+A.
    All,

    // A plain left-click that landed on nothing.
    Clear
}

// Selected is in the order the list is showing, never the order the rows were picked in. Anchor is
// the row a Shift+click extends from, which is not the same thing as the row the list is drawing as
// its own selection.
public sealed record ListSelectionState<T>(IReadOnlyList<T> Selected, T? Anchor)
    where T : class
{
    public static ListSelectionState<T> Empty { get; } = new([], null);
}

public static class ListSelection
{
    public static ListSelectionState<T> Apply<T>(
        ListSelectionGesture gesture,
        IReadOnlyList<T> shown,
        ListSelectionState<T> current,
        T? target)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(current);

        // Nothing was clicked, so nothing survives, the anchor included: an anchor pointing into a
        // selection that no longer exists would silently decide the next Shift+click's range.
        if (gesture is ListSelectionGesture.Clear)
            return ListSelectionState<T>.Empty;

        // A row a filter, a search or a collapsed section is hiding is not one a gesture can reach,
        // so the incoming set and anchor are narrowed to what is on screen before anything else.
        var visible = new HashSet<T>(shown);
        var selected = new HashSet<T>(current.Selected.Where(visible.Contains));
        var anchor = current.Anchor is { } held && visible.Contains(held) ? held : null;

        if (gesture is ListSelectionGesture.All)
            return new ListSelectionState<T>([.. shown], anchor ?? shown.FirstOrDefault());

        if (target is null || !visible.Contains(target))
            return new ListSelectionState<T>(Ordered(shown, selected), anchor);

        switch (gesture)
        {
            case ListSelectionGesture.Replace:
                return new ListSelectionState<T>([target], target);

            case ListSelectionGesture.Toggle:
                if (!selected.Remove(target))
                    selected.Add(target);

                return new ListSelectionState<T>(Ordered(shown, selected), target);

            case ListSelectionGesture.Extend:
            case ListSelectionGesture.ExtendAdd:
                // Nothing to extend from means the first row on screen is the anchor, which is what
                // Explorer does when a Shift+click is the first thing done in a list.
                var from = anchor ?? shown[0];
                var range = Between(shown, from, target);

                if (gesture is ListSelectionGesture.Extend)
                    selected.Clear();

                selected.UnionWith(range);

                // The anchor stays where it is, so a second Shift+click re-measures from the same row
                // rather than growing the range one click at a time.
                return new ListSelectionState<T>(Ordered(shown, selected), from);

            default:
                throw new ArgumentOutOfRangeException(nameof(gesture), gesture, "Unknown selection gesture.");
        }
    }

    private static IReadOnlyList<T> Ordered<T>(IReadOnlyList<T> shown, HashSet<T> selected)
        where T : class =>
        [.. shown.Where(selected.Contains)];

    private static IReadOnlyList<T> Between<T>(IReadOnlyList<T> shown, T from, T to)
        where T : class
    {
        var first = IndexOf(shown, from);
        var last = IndexOf(shown, to);

        if (first < 0 || last < 0)
            return [];

        var (start, end) = first <= last ? (first, last) : (last, first);
        var range = new List<T>(end - start + 1);

        for (var i = start; i <= end; i++)
            range.Add(shown[i]);

        return range;
    }

    private static int IndexOf<T>(IReadOnlyList<T> shown, T row)
        where T : class
    {
        for (var i = 0; i < shown.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(shown[i], row))
                return i;
        }

        return -1;
    }
}
