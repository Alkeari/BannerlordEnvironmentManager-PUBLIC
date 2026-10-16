namespace BannerlordEnvironmentManager.Core.Instances;

// Why one action on a version row cannot run. Only facts that hold for as long as a menu stays open
// are here: a running game and a live junction are checked again at the press and answered with a
// sentence, because either can begin while the menu is open, and an item grayed out for a reason
// that has since passed reads as a control that does nothing.
public enum InstanceActionState
{
    Allowed,
    NoInstance,
    NoPath,
    FolderMissing,
    AlreadyActive,
    AlreadyResting,
    IsResting,
    NotOffered,
    CatalogNotRead,
    RestingIsForPlaying
}

public sealed record InstanceMenuState(
    InstanceActionState Rename,
    InstanceActionState SetPurpose,
    InstanceActionState OpenFolder,
    InstanceActionState CopyPath,
    InstanceActionState SetActive,
    InstanceActionState SetResting,
    InstanceActionState Remove,
    InstanceActionState ShowDetails,
    InstanceActionState InstallAnotherCopy);

// Which of the nine things a version row offers can actually be done to it. Every item is offered
// on every row: one that cannot apply is disabled carrying its reason, never hidden and never left
// to be pressed and do nothing.
public static class InstanceMenu
{
    public static InstanceMenuState Nothing { get; } = new(
        InstanceActionState.NoInstance,
        InstanceActionState.NoInstance,
        InstanceActionState.NoInstance,
        InstanceActionState.NoInstance,
        InstanceActionState.NoInstance,
        InstanceActionState.NoInstance,
        InstanceActionState.NoInstance,
        InstanceActionState.NoInstance,
        InstanceActionState.NoInstance);

    // isOffered is whether Steam still publishes this row's own version and variant. A second copy
    // is a fresh download of exactly that pair, so a version that has left the branch list has
    // nowhere to fetch one from and the item says so rather than failing at the press.
    //
    // Null is the third answer and the honest one until the branch list has been fetched: not
    // knowing whether Steam publishes a version is not the same as knowing it does not, and the
    // item said the second over the first for as long as the fetch took, or forever with no
    // connection. A disabled control that gives a false reason is worse than one that gives none.
    public static InstanceMenuState For(
        InstanceRecord? record, bool isActive, bool isResting, bool gameFolderExists, bool? isOffered = null)
    {
        if (record is null)
            return Nothing;

        var hasPath = !string.IsNullOrWhiteSpace(record.GameFolder);

        return new InstanceMenuState(
            Rename: InstanceActionState.Allowed,
            // The resting instance is the user's own game whatever a record says, so declaring a
            // purpose on it is refused rather than accepted and then overridden: the one thing this
            // field decides is where a mod build may be installed, and an instance that could be
            // marked for testing while resting is the failure it exists to stop.
            SetPurpose: isResting ? InstanceActionState.RestingIsForPlaying : InstanceActionState.Allowed,
            OpenFolder: !hasPath
                ? InstanceActionState.NoPath
                : gameFolderExists
                    ? InstanceActionState.Allowed
                    : InstanceActionState.FolderMissing,
            // A path BEM wrote down is worth copying whether or not its drive is attached right now:
            // pasting it into a mail or a bug report is the point, and a disconnected drive does not
            // make the path wrong.
            CopyPath: hasPath ? InstanceActionState.Allowed : InstanceActionState.NoPath,
            SetActive: isActive ? InstanceActionState.AlreadyActive : InstanceActionState.Allowed,
            SetResting: isResting ? InstanceActionState.AlreadyResting : InstanceActionState.Allowed,
            // Being referenced never blocks either one: the adopted Steam install is a resting
            // instance like any other, and InstanceManager.SetResting moves state rather than files.
            // The resting instance is the one thing that cannot be removed, and that is a decision
            // the user can act on rather than a wait, so it is stated up front.
            Remove: isResting ? InstanceActionState.IsResting : InstanceActionState.Allowed,
            ShowDetails: InstanceActionState.Allowed,
            InstallAnotherCopy: isOffered switch
            {
                true => InstanceActionState.Allowed,
                false => InstanceActionState.NotOffered,
                null => InstanceActionState.CatalogNotRead
            });
    }
}
