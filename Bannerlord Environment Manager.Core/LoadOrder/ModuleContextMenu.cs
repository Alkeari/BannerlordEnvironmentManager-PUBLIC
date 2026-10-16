namespace BannerlordEnvironmentManager.Core.LoadOrder;

// Every item the module row's context menu can offer, named once so the view and this decision
// cannot drift apart.
public enum ModuleMenuItem
{
    Toggle,
    EnableWithDependencies,
    DisableWithDependents,
    Uninstall,
    Prune,
    MoveToTop,
    MoveToBottom,
    MoveUp,
    MoveDown,
    Update,
    OpenModPage,
    TogglePin,
    OpenFolder,
    OpenManifest,
    CopyId,
    InsertDividerAbove,
    InsertDividerBelow,
    SetNexusModId,
    ForgetModId,
    NotOnNexus,
    Deselect,
    SelectAll,
    ClearSelection
}

// The right-clicked row in the terms the menu decides by, and nothing else: no paths, no view
// models, no launcher state. Everything that only grays an item out (the game running, a download in
// flight, the row already at the top) is deliberately absent, because a blocked action still appears
// and carries its reason.
public sealed record ModuleMenuRow(
    bool IsOrphan,
    bool HasUpdate,
    bool HasConfirmedModId,
    bool IsUnidentified,
    bool IsMarkedNotOnNexus,
    bool NeedsDependenciesEnabled,
    bool HasEnabledDependents,
    bool IsWorkshopSubscription = false);

// Which items a row offers, and in what order.
//
// Two menus off one list, the way Windows Explorer does it: a plain right-click carries what the user
// came to do, and Shift+right-click reveals the maintenance and the rarities in place rather than
// nesting them in a submenu. A conditional item sits in the plain menu when its appearing at all means
// it is the reason they right-clicked, which is why an orphan's Prune and a selection's Deselect are
// there and Copy Module Id is not.
//
// Hidden is for an action with nothing to act on: no update exists, no confirmed id to forget,
// nothing depends on this module. That is not a disabled control, it is an item with no meaning for
// this row. An action that is merely blocked (the row is already at the top, there is no folder to
// open, the game is running) stays visible and disabled, carrying its reason in its tooltip, because
// hiding those would leave the reader hunting for something they have seen before.
public static class ModuleContextMenu
{
    // The six groups a separator sits between, in menu order. Empty groups are still returned so the
    // caller can line its separators up against them; the flattened form drops them.
    public static IReadOnlyList<IReadOnlyList<ModuleMenuItem>> Groups(
        ModuleMenuRow? row, bool inMultiSelection, bool extended)
    {
        // A right-click on empty space below the last row can only mean one thing, so the selection
        // items are its plain menu rather than something to hold Shift for.
        if (row is null)
            return [[], [], [], [], [], [ModuleMenuItem.SelectAll, ModuleMenuItem.ClearSelection]];

        // A batch replaces the menu rather than joining it: the counted items the view builds are
        // what a multi-row selection offers, and the only things standing beside them choose what a
        // batch will reach.
        return inMultiSelection
            ? [[], [], [], [], [], Selection(true, extended)]
            : [Actions(row), Order(extended), Mod(row), Tools(extended), Identity(row, extended), Selection(false, extended)];
    }

    public static IReadOnlyList<ModuleMenuItem> For(ModuleMenuRow? row, bool inMultiSelection, bool extended) =>
        [.. Groups(row, inMultiSelection, extended).SelectMany(group => group)];

    private static IReadOnlyList<ModuleMenuItem> Actions(ModuleMenuRow row)
    {
        var items = new List<ModuleMenuItem> { ModuleMenuItem.Toggle };

        // The closure items earn their place only when they reach further than the plain Enable or
        // Disable directly above them. A module whose dependencies are all on already, or that
        // nothing enabled needs, is served by that one item.
        if (row.NeedsDependenciesEnabled)
            items.Add(ModuleMenuItem.EnableWithDependencies);

        if (row.HasEnabledDependents)
            items.Add(ModuleMenuItem.DisableWithDependents);

        items.Add(ModuleMenuItem.Uninstall);

        // Pruning is for a launcher entry whose folder is gone, and it is the only interesting thing
        // to do to one, so it stays in the plain menu. Every installed module has nothing to prune.
        if (row.IsOrphan)
            items.Add(ModuleMenuItem.Prune);

        return items;
    }

    // Dragging covers small adjustments, so the two ends are the plain menu's and the two nudges are
    // revealed beside them.
    private static IReadOnlyList<ModuleMenuItem> Order(bool extended) =>
        extended
            ? [ModuleMenuItem.MoveToTop, ModuleMenuItem.MoveToBottom, ModuleMenuItem.MoveUp, ModuleMenuItem.MoveDown]
            : [ModuleMenuItem.MoveToTop, ModuleMenuItem.MoveToBottom];

    private static IReadOnlyList<ModuleMenuItem> Mod(ModuleMenuRow row) =>
        row.HasUpdate
            ? [ModuleMenuItem.Update, ModuleMenuItem.OpenModPage, ModuleMenuItem.TogglePin]
            : [ModuleMenuItem.OpenModPage, ModuleMenuItem.TogglePin];

    private static IReadOnlyList<ModuleMenuItem> Tools(bool extended) =>
        extended
            ?
            [
                ModuleMenuItem.OpenFolder,
                ModuleMenuItem.OpenManifest,
                ModuleMenuItem.CopyId,
                ModuleMenuItem.InsertDividerAbove,
                ModuleMenuItem.InsertDividerBelow
            ]
            : [];

    private static IReadOnlyList<ModuleMenuItem> Identity(ModuleMenuRow row, bool extended)
    {
        if (!extended)
            return [];

        // None of this group is a question about a subscribed item. Steam publishes it, so there is no
        // Nexus mod id to name or to take back, and saying it is not published on Nexus states nothing
        // about a mod that was never offered there. A statement already recorded is the one exception,
        // for the same reason it survives its own condition below: the item is how it is taken back.
        if (row.IsWorkshopSubscription)
            return row.IsMarkedNotOnNexus ? [ModuleMenuItem.NotOnNexus] : [];

        var items = new List<ModuleMenuItem> { ModuleMenuItem.SetNexusModId };

        if (row.HasConfirmedModId)
            items.Add(ModuleMenuItem.ForgetModId);

        // The statement is offered to a module BEM cannot name a mod page for, and stays offered to
        // one already carrying it, because the same item is how it is taken back.
        if (row.IsUnidentified || row.IsMarkedNotOnNexus)
            items.Add(ModuleMenuItem.NotOnNexus);

        return items;
    }

    // Ctrl+A, Shift+click and Ctrl+click all build a selection now, so building one from the menu is
    // a rarity. Taking one row back out of a set is not: it is the reason for that right-click.
    private static IReadOnlyList<ModuleMenuItem> Selection(bool inMultiSelection, bool extended)
    {
        var items = new List<ModuleMenuItem>();

        if (inMultiSelection)
            items.Add(ModuleMenuItem.Deselect);

        if (extended)
        {
            items.Add(ModuleMenuItem.SelectAll);
            items.Add(ModuleMenuItem.ClearSelection);
        }

        return items;
    }
}
