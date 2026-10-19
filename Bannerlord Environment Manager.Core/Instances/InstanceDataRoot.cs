namespace BannerlordEnvironmentManager.Core.Instances;

// The one place that knows how the game's user data is shaped. Everything that used to build a path
// out of the literal "Mount and Blade II Bannerlord" takes one of these instead, so a screen shows the
// instance BEM is displaying rather than whatever happens to sit at the default path right now.
public sealed record InstanceDataRoot(string Documents, string ProgramData)
{
    public static InstanceDataRoot ForMachine()
    {
        var paths = CanonicalPathSet.ForMachine();

        return new InstanceDataRoot(paths.Documents, paths.ProgramData);
    }

    // The instance folder this root came from, when it came from one. Null on the machine root, which
    // belongs to no version in particular. BEM's own files about a load order - the pins, the sections,
    // the saved profiles, the accepted findings and risks, the launch history, the settings
    // attributions - are keyed on this: each of them describes one version's list and says nothing true
    // about another's.
    public string? InstanceFolder { get; init; }

    public static InstanceDataRoot ForInstance(string instanceFolder) => new(
        InstanceLayout.StoreFolder(instanceFolder, "Documents"),
        InstanceLayout.StoreFolder(instanceFolder, "ProgramData"))
    {
        InstanceFolder = instanceFolder
    };

    // The root belonging to a given game folder, rather than to whichever instance happens to be
    // active. A screen that scans one install's folders and then reads another instance's load order
    // sees every module on disk as not enabled, and every check that skips a disabled module reports
    // nothing at all, which reads as a clean install rather than as a check that never looked.
    // Null for the resting install, whose user data really does sit at the canonical machine paths,
    // and for a folder no instance owns.
    public static InstanceDataRoot? ForGameFolder(
        IReadOnlyList<InstalledInstance> instances,
        string? gameFolder,
        string? restingInstanceId)
    {
        ArgumentNullException.ThrowIfNull(instances);

        if (string.IsNullOrWhiteSpace(gameFolder))
            return null;

        var wanted = Normalize(gameFolder);

        foreach (var instance in instances)
        {
            if (!string.Equals(Normalize(instance.Record.GameFolder), wanted, StringComparison.OrdinalIgnoreCase))
                continue;

            return string.Equals(instance.Record.Id, restingInstanceId, StringComparison.Ordinal)
                ? null
                : ForInstance(instance.Folder);
        }

        return null;
    }

    // Whether any instance owns that folder at all, which null from ForGameFolder cannot say on its
    // own: the resting install answers null because its data really is at the canonical paths, and a
    // folder belonging to no instance answers null because nothing was found. A caller that falls
    // back when it finds nothing has to be able to tell those two apart.
    public static bool Owns(IReadOnlyList<InstalledInstance> instances, string? gameFolder)
    {
        ArgumentNullException.ThrowIfNull(instances);

        if (string.IsNullOrWhiteSpace(gameFolder))
            return false;

        var wanted = Normalize(gameFolder);

        return instances.Any(instance =>
            string.Equals(Normalize(instance.Record.GameFolder), wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string? path) => string.IsNullOrWhiteSpace(path)
        ? string.Empty
        : Path.TrimEndingDirectorySeparator(path.Trim());

    public string Configs => Path.Combine(Documents, "Configs");

    public string GameSaves => Path.Combine(Documents, "Game Saves");

    public string Logs => Path.Combine(Documents, "Logs");

    public string Shaders => Path.Combine(ProgramData, "Shaders");
}
