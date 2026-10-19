using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.GameSettings;

// An instance and where its base game settings actually live right now. Those are two different
// facts and must not be derived from one another.
//
// The frozen copy is always inside the instance folder, because it is BEM's own record and belongs
// to that instance wherever the game's files happen to be. The live files are not: the resting
// install reads and writes the canonical Documents\Mount and Blade II Bannerlord path, while a
// managed instance reads its own UserData folder. Composing the live path from the instance folder
// would silently write the resting install's settings into a folder the game never reads.
//
// This is the one place those rules are written down. It is the same predicate the Play page already
// uses to decide an instance's data root, kept here so the launch hooks, the Settings sweep and the
// tests all ask the same question and get the same answer.
public sealed record GameSettingsTarget(string InstanceFolder, InstanceDataRoot DataRoot)
{
    public string Configs => DataRoot.Configs;

    public static GameSettingsTarget For(InstalledInstance instance, string? restingInstanceId)
    {
        ArgumentNullException.ThrowIfNull(instance);

        return For(instance.Folder, instance.Record.Id, restingInstanceId);
    }

    public static GameSettingsTarget For(string instanceFolder, string instanceId, string? restingInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceFolder);

        return new GameSettingsTarget(
            instanceFolder,
            string.Equals(instanceId, restingInstanceId, StringComparison.Ordinal)
                ? InstanceDataRoot.ForMachine()
                : InstanceDataRoot.ForInstance(instanceFolder));
    }
}
