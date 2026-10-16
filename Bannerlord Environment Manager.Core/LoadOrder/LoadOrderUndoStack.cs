using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

public sealed record LoadOrderUndoStep(ModuleEnvironment Order, string Description);

// The whole environment is kept rather than an id-and-flag snapshot, because a prune takes rows out
// of the list entirely and a snapshot could only ever put back rows that are still there.
public sealed class LoadOrderUndoStack
{
    public static string NothingToUndo => Strings.Current["Core.LoadOrder.UndoStack.NothingToUndo"];

    private LoadOrderUndoStep? pending;

    public bool CanUndo => pending is not null;

    public string Reason => pending is null
        ? NothingToUndo
        : Strings.Current.Format("Core.LoadOrder.UndoStack.Reason", pending.Description);

    public void Record(ModuleEnvironment order, string description)
    {
        ArgumentNullException.ThrowIfNull(order);

        pending = new LoadOrderUndoStep(order, description);
    }

    // One level, so the step is consumed. Offering the same undo twice would put the list back to a
    // state it is already in and report a change that did not happen.
    public LoadOrderUndoStep? Take()
    {
        var step = pending;
        pending = null;

        return step;
    }

    public void Clear() => pending = null;
}
