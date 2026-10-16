namespace BannerlordEnvironmentManager.Core.Modules;

// Tracks whether a Modules folder change was skipped because a load-order write was being held back
// (the game or its launcher was running), so that skipped refresh runs exactly once when the hold
// clears, instead of being lost until whatever the caller does next happens to trigger one of its own.
// Holds no reference to any view model, watcher, or UI thread, so it is driven and tested directly.
public sealed class HeldChangeCatchUp
{
    private bool missed;

    // A change arrived while a hold was in force and its refresh was skipped. Several missed changes
    // before the hold clears still owe only the one refresh a real rescan would satisfy for all of them.
    public void RecordMissedWhileHeld() => missed = true;

    // The hold has just cleared. Returns true exactly once per RecordMissedWhileHeld call (or run of
    // them), then false until another change is missed - never true again for a hold that never had
    // anything missed during it.
    public bool ShouldCatchUp()
    {
        if (!missed)
            return false;

        missed = false;
        return true;
    }
}
