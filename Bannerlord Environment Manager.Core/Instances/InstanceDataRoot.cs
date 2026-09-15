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

    public string Configs => Path.Combine(Documents, "Configs");

    public string GameSaves => Path.Combine(Documents, "Game Saves");

    public string Logs => Path.Combine(Documents, "Logs");

    public string Shaders => Path.Combine(ProgramData, "Shaders");
}
