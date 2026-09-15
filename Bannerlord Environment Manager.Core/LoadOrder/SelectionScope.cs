namespace BannerlordEnvironmentManager.Core.LoadOrder;

// A selection belongs to the install it was made in. The Play page rebuilds its rows for two very
// different reasons and restores the selection by module id either way: after a rescan of the same
// install that is exactly right, and it is what stops a background refresh wiping a set the user is
// halfway through building. After a switch to another instance it is wrong, because the same module
// ids sit in every instance: Harmony, ButterLib, UIExtenderEx and MBOptionScreen would come back
// selected in a game folder nobody picked them in, and a batch action would then act on rows the
// user never chose. This is the one fact that tells the two rebuilds apart.
public static class SelectionScope
{
    public static bool Carries(string? madeUnder, string? rebuildingUnder) =>
        !string.IsNullOrWhiteSpace(madeUnder)
        && !string.IsNullOrWhiteSpace(rebuildingUnder)
        && string.Equals(madeUnder, rebuildingUnder, StringComparison.OrdinalIgnoreCase);
}
