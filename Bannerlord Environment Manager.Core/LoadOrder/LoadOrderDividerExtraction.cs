using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// The single recognition algorithm behind every entry point that reads an ordered list of raw ids
// and might find a divider-* id sitting in it: LoadOrderSnapshot.ApplyTo (a snapshot saved before
// this feature existed), ModuleEnvironment (a real installed dummy divider folder), and
// LoadOrderImport.Preview (someone else's shared load order file). Each divider found is anchored to
// the next id after it that isResolvable accepts, skipping any other divider ids and any id
// isResolvable rejects along the way. A divider with nothing resolvable left after it anchors to the
// end of the list (a null AnchorId), never dropped.
public static class LoadOrderDividerExtraction
{
    public static (IReadOnlyList<string> RealIds, IReadOnlyList<LoadOrderDivider> Dividers) Extract(
        IReadOnlyList<string> ids,
        Func<string, bool> isResolvable)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(isResolvable);

        var realIds = new List<string>(ids.Count);
        var dividers = new List<LoadOrderDivider>();
        var pendingLabels = new List<string>();

        foreach (var id in ids)
        {
            if (DividerConvention.TryParseLabel(id) is { } label)
            {
                pendingLabels.Add(label);
                continue;
            }

            realIds.Add(id);

            if (pendingLabels.Count == 0 || !isResolvable(id))
                continue;

            var anchor = new ModuleId(id);

            foreach (var pending in pendingLabels)
                dividers.Add(new LoadOrderDivider(pending, anchor));

            pendingLabels.Clear();
        }

        foreach (var pending in pendingLabels)
            dividers.Add(new LoadOrderDivider(pending, AnchorId: null));

        return (realIds, dividers);
    }
}
