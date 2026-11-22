using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Instances;

// One instance whose recorded version did not match the game installed under it, and what it was
// corrected to.
public sealed record GameVersionReconciliationChange(string InstanceId, string Name, string Was, string Now);

public sealed record GameVersionReconciliationReport(IReadOnlyList<GameVersionReconciliationChange> Changed)
{
    public bool AnythingChanged => Changed.Count > 0;

    public string Describe()
    {
        if (!AnythingChanged)
            return Strings.Current["Core.Instances.VersionReconcile.Nothing"];

        var listed = string.Join(", ", Changed.Select(change => Strings.Current.Format(
            "Core.Instances.VersionReconcile.Item", change.Name, change.Was, change.Now)));

        return Strings.Current.Plural("Core.Instances.VersionReconcile.Corrected", Changed.Count, listed)
            + " " + Strings.Current["Core.Instances.VersionReconcile.Why"];
    }
}

// InstanceRecord.RecordedGameVersion is written once, by whatever put the instance down, and Steam
// updates an install it owns without asking BEM: an adopted install recorded at v1.4.8 keeps saying
// v1.4.8 through every patch, while the Native module underneath it says otherwise. This is the
// write-back half of what VersionDrift already answers for display, so the record stops disagreeing
// with the game files it points at.
public static class GameVersionReconciliation
{
    // What this record's RecordedGameVersion should say, or null when it should be left exactly as it
    // is.
    //
    // Only a referenced install is looked at. A managed instance's Game folder is one BEM downloaded
    // into and nothing but BEM writes there, so a disagreement is not an update that happened behind
    // BEM's back; re-deriving it would put the folder-naming pass onto a folder the download named on
    // purpose, for a version nothing changed.
    public static string? For(InstanceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!record.GameFolderIsReferenced)
            return null;

        var drift = VersionDrift.Detect(record, InstalledVersion(record.GameFolder), []);

        return drift.Drifted ? drift.InstalledVersion : null;
    }

    // A Steam install that is mid-update, offline or on a drive that is not mounted answers nothing,
    // and nothing is what VersionDrift already reads as BEM being unable to look rather than as a
    // version having changed. The catch is for the paths the reader cannot guard on its own: a game
    // folder that is no longer a legal path at all still has to leave the record standing.
    private static string InstalledVersion(string gameFolder)
    {
        try
        {
            return GameVersionReader.Read(gameFolder).ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return string.Empty;
        }
    }
}
