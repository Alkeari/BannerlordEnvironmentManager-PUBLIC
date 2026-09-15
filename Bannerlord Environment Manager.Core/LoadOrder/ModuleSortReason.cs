using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// Why one module ended up where it did. The two causes are different claims and neither may be made of
// the other: a declared edge decided the module could go no earlier, or the tie-break plan chose it
// where nothing was declared at all. On a real install the second decides the overwhelming
// majority, which is exactly why saying which is which matters.
public enum SortReasonKind
{
    // A declaration decided it: the module was placed the moment the last thing it must load after had
    // been placed, and could not have gone any earlier.
    Constraint,

    // Nothing it declares reached this far. The sort keys chose it ahead of the module named.
    SortKey,

    // It was the only module free to take the position, so no key had anything to choose between.
    Unopposed,

    // The user pinned it, so the sort kept it where it was.
    Pinned
}

public sealed record ModuleSortReason(
    ModuleId Id,
    int From,
    int To,
    SortReasonKind Kind,
    ModuleId? Other = null,
    ModuleSortKey? Key = null)
{
    public bool Moved => From != To;

    public int Distance => Math.Abs(To - From);

    public string Describe() => $"{Where()}: {Why()}";

    private string Where() => Moved
        ? Strings.Current.Format("Core.LoadOrder.SortReason.Moved", Id, From + 1, To + 1)
        : Strings.Current.Format("Core.LoadOrder.SortReason.Kept", Id, From + 1);

    private string Why() => Kind switch
    {
        SortReasonKind.Constraint => Strings.Current.Format("Core.LoadOrder.SortReason.Constraint", Other),
        SortReasonKind.Pinned => Strings.Current["Core.LoadOrder.SortReason.Pinned"],
        SortReasonKind.Unopposed => Strings.Current["Core.LoadOrder.SortReason.Unopposed"],
        _ => Key is { } key
            ? Strings.Current.Format("Core.LoadOrder.SortReason.SortKey", Other, ModuleSortPlan.Describe(key))
            : Strings.Current.Format("Core.LoadOrder.SortReason.Tied", Other)
    };
}
