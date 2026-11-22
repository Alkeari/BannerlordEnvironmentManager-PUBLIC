using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public enum RemedyStatus
{
    Ready,
    AlreadySatisfied,
    ModuleMissing,
    WouldBreakADeclaredOrder
}

public sealed record LoadOrderRemedy(
    RemedyStatus Status,
    IReadOnlyList<ModuleEntry> Corrected,
    string Message)
{
    public bool IsReady => Status == RemedyStatus.Ready;
}

// The one remedy every load-order diagnosis on the Diagnostics tab shares: move one module below
// another and leave everything else where the user put it. A shadowed assembly is fixed by dropping
// the module holding the older copy below the one that wants the newer one; an XML dataset is won by
// whichever module sorts last, so making a different one win is the same move.
public static class LoadOrderRemedies
{
    public static LoadOrderRemedy PlanMoveBelow(
        IReadOnlyList<ModuleEntry> loadOrder,
        ModuleId mover,
        ModuleId anchor)
    {
        ArgumentNullException.ThrowIfNull(loadOrder);

        var moverIndex = IndexOf(loadOrder, mover);
        var anchorIndex = IndexOf(loadOrder, anchor);

        if (moverIndex < 0 || anchorIndex < 0)
        {
            var missing = moverIndex < 0 ? mover : anchor;

            return new LoadOrderRemedy(
                RemedyStatus.ModuleMissing,
                loadOrder,
                Strings.Current.Format("Core.Modules.LoadOrderRemedy.ModuleMissing", missing));
        }

        if (moverIndex > anchorIndex)
        {
            return new LoadOrderRemedy(
                RemedyStatus.AlreadySatisfied,
                loadOrder,
                Strings.Current.Format("Core.Modules.LoadOrderRemedy.AlreadySatisfied", mover, anchor));
        }

// A mod that must load after the mover goes with it, and so on down the chain, each keeping its place
        // relative to the others. Only the anchor cannot be carried: if it must load after the mover, the
        // mover can never load below it, and that is said plainly.
        var carried = new HashSet<ModuleId> { mover };

        while (true)
        {
            var corrected = MovedBelow(loadOrder, carried, anchor);
            var broken = LoadOrderDrag.Introduced(loadOrder, corrected);

            if (broken.Count == 0)
            {
                var others = loadOrder.Where(e => carried.Contains(e.Id) && e.Id != mover).Select(e => e.Id.ToString()).ToList();

                return new LoadOrderRemedy(
                    RemedyStatus.Ready,
                    corrected,
                    others.Count == 0
                        ? Strings.Current.Format("Core.Modules.LoadOrderRemedy.Ready", mover, anchor)
                        : Strings.Current.Format("Core.Modules.LoadOrderRemedy.ReadyCarried", mover, anchor, string.Join(", ", others)));
            }

            var toCarry = broken.Select(v => v.Later).Where(id => id != anchor && carried.Contains(id) == false && IndexOf(loadOrder, id) >= 0).ToList();

            if (broken.Any(v => v.Later == anchor) || toCarry.Count == 0)
            {
                return new LoadOrderRemedy(
                    RemedyStatus.WouldBreakADeclaredOrder,
                    loadOrder,
                    Strings.Current.Format("Core.Modules.LoadOrderRemedy.WouldBreakOrder", mover, anchor, LoadOrderDrag.Describe(broken)));
            }

            carried.UnionWith(toCarry);
        }
    }

    private static List<ModuleEntry> MovedBelow(IReadOnlyList<ModuleEntry> loadOrder, HashSet<ModuleId> moved, ModuleId anchor)
    {
        var result = loadOrder.Where(e => !moved.Contains(e.Id)).ToList();
        var at = result.FindIndex(e => e.Id == anchor) + 1;

        result.InsertRange(at, loadOrder.Where(e => moved.Contains(e.Id)));

        return result;
    }

    private static int IndexOf(IReadOnlyList<ModuleEntry> loadOrder, ModuleId id)
    {
        for (var i = 0; i < loadOrder.Count; i++)
        {
            if (loadOrder[i].Id == id)
                return i;
        }

        return -1;
    }
}
