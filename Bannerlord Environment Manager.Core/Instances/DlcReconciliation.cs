using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Instances;

// One instance whose recorded DLC set did not match its own files, and what it was corrected to.
public sealed record DlcReconciliationChange(string InstanceId, string Name, GameDlcSet Was, GameDlcSet Now);

public sealed record DlcReconciliationReport(IReadOnlyList<DlcReconciliationChange> Changed)
{
    public bool AnythingChanged => Changed.Count > 0;

    public string Describe()
    {
        if (!AnythingChanged)
            return Strings.Current["Core.Instances.DlcReconcile.Nothing"];

        var listed = string.Join(", ", Changed.Select(change => Strings.Current.Format(
            "Core.Instances.DlcReconcile.Item", change.Name, Contents(change.Now))));

        return Strings.Current.Plural("Core.Instances.DlcReconcile.Corrected", Changed.Count, listed)
            + " " + Strings.Current["Core.Instances.DlcReconcile.FolderUnchanged"];
    }

    private static string Contents(GameDlcSet dlc) => dlc.IsEmpty
        ? Strings.Current["Core.Instances.DlcReconcile.BaseGame"]
        : string.Join(" + ", dlc.DisplayNames);
}

// InstanceRecord.Dlc is written once, by whatever put the instance down, and Steam can add a DLC to a
// referenced install without BEM being asked: a real adopted v1.4.8 records no DLC while its
// Modules folder holds War Sails. This is the write-back half of what GameDlcDetector already answers
// for display, so the record stops disagreeing with the files underneath it.
public static class DlcReconciliation
{
    // What this record's Dlc should say, or null when it should be left exactly as it is.
    //
    // The disk wins, with two carve-outs. A game folder that cannot be resolved at all means BEM could
    // not look, which is not the same as a DLC being gone: a referenced install on a drive that is
    // disconnected or renamed would otherwise be rewritten to base-only and offered a 31 GB redownload
    // of a DLC still sitting on it. And an app id BEM has no row for in GameDlc.Known is carried over
    // untouched, because detection cannot look for a DLC it does not know the module folder of, and
    // dropping it would read as removing a DLC rather than as never having checked for one.
    public static GameDlcSet? For(InstanceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!GameDlcDetector.TryDetect(record.GameFolder, out var detected))
            return null;

        var undetectable = record.Dlc.AppIds.Where(id => GameDlc.ById(id) is null);
        var reconciled = new GameDlcSet([.. detected.AppIds, .. undetectable]);

        return reconciled == record.Dlc ? null : reconciled;
    }
}
