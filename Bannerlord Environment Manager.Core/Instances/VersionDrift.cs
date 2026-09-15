using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Instances;

// A referenced install belongs to Steam, which can update it or switch its branch without asking BEM.
// Drift is a comparison against what the instance recorded, never an inference from a folder name, and
// BEM decides nothing on its own: this type reports, and the user picks what happens next.
public sealed record VersionDrift(
    bool Drifted,
    string RecordedVersion,
    string InstalledVersion,
    IReadOnlyList<string> SavesFromOtherVersion)
{
    public static VersionDrift Detect(
        InstanceRecord record,
        string installedVersion,
        IReadOnlyList<(string Name, string ApplicationVersion)> saves)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(saves);

        // An unreadable version means BEM could not look, which is not the same as a version change.
        if (string.IsNullOrWhiteSpace(installedVersion))
            return new VersionDrift(false, record.RecordedGameVersion, record.RecordedGameVersion, []);

        var drifted = !string.Equals(
            record.RecordedGameVersion, installedVersion, StringComparison.OrdinalIgnoreCase);

        var affected = drifted
            ? saves
                .Where(s => !string.Equals(s.ApplicationVersion, installedVersion, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Name)
                .ToArray()
            : [];

        return new VersionDrift(drifted, record.RecordedGameVersion, installedVersion, affected);
    }

    public string Describe() => Drifted
        ? Strings.Current.Format("Core.Instances.VersionDrift.Changed", RecordedVersion, InstalledVersion)
          + " " + Strings.Current.Plural("Core.Instances.VersionDrift.SavesFromOlder", SavesFromOtherVersion.Count)
        : Strings.Current["Core.Instances.VersionDrift.Unchanged"];
}
