namespace BannerlordEnvironmentManager.Core.LoadOrder;

// Auto-Sort itself (LoadOrderSorter) never learns dividers exist - this is the one place their
// anchors change because of a sort, and it runs as a separate step alongside it, never inside it.
public static class LoadOrderDividerAutoSortPolicy
{
    public static IReadOnlyList<LoadOrderDivider> Apply(
        IReadOnlyList<LoadOrderDivider> dividers, LoadOrderDividerOptions options)
    {
        ArgumentNullException.ThrowIfNull(dividers);
        ArgumentNullException.ThrowIfNull(options);

        return options.KeepAnchoredOnAutoSort
            ? dividers
            : [.. dividers.Select(d => d with { AnchorId = null })];
    }
}
