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

        var corrected = new List<ModuleEntry>(loadOrder);
        corrected.RemoveAt(moverIndex);
        corrected.Insert(anchorIndex, loadOrder[moverIndex]);

        // A fix that quietly breaks a declared edge would trade a diagnosis the user can see for one
        // the engine enforces silently, so it refuses and says which edge stopped it.
        var broken = LoadOrderDrag.Introduced(loadOrder, corrected);

        if (broken.Count > 0)
        {
            return new LoadOrderRemedy(
                RemedyStatus.WouldBreakADeclaredOrder,
                loadOrder,
                Strings.Current.Format("Core.Modules.LoadOrderRemedy.WouldBreakOrder", mover, anchor, LoadOrderDrag.Describe(broken)));
        }

        return new LoadOrderRemedy(
            RemedyStatus.Ready,
            corrected,
            Strings.Current.Format("Core.Modules.LoadOrderRemedy.Ready", mover, anchor));
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
