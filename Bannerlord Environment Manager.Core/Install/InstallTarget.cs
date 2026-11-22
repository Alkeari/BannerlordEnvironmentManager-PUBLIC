using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Install;

// One place a mod archive is being installed into: which instance it is, the game folder whose
// Modules receive it, where that instance's user data actually lives, and what to call it when a
// result has to name it.
//
// The four are carried rather than derived from one another, for the same reason the game settings
// target carries its three. The resting install reads and writes the canonical machine path, so
// composing its data root from its instance folder would aim the archive links, the bin backups and
// the quarantine records at a folder nothing reads. The game folder is a fact again: a referenced
// instance points at a folder Steam owns, which is nowhere under the instance folder BEM made for it.
//
// Before this, the destination was ambient - one install path and one data root held on the Library
// page - so every installer in Core already took a path and the page supplied the same one to all of
// them. Installing into several versions at once is what turns that into a value, and a value is what
// makes it impossible to write the second version's files against the first version's paths.
//
// The name rides along so a per-instance result can say which one failed in the words the user sees
// in Play's dropdown, rather than an id or a path they have to translate.
public sealed record InstallTarget(
    string InstanceId,
    string InstanceFolder,
    string GameFolder,
    InstanceDataRoot DataRoot,
    string DisplayName)
{
    public string ModulesFolder => ModuleScanner.GetModulesFolder(GameFolder);

    public static InstallTarget For(InstalledInstance instance, string? restingInstanceId)
    {
        ArgumentNullException.ThrowIfNull(instance);

        return For(
            instance.Folder,
            instance.Record.Id,
            restingInstanceId,
            instance.Record.GameFolder,
            InstanceLabel.NameOf(instance.Record));
    }

    public static InstallTarget For(
        string instanceFolder,
        string instanceId,
        string? restingInstanceId,
        string gameFolder,
        string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);

        return new InstallTarget(
            instanceId,
            instanceFolder,
            gameFolder,
            string.Equals(instanceId, restingInstanceId, StringComparison.Ordinal)
                ? InstanceDataRoot.ForMachine()
                : InstanceDataRoot.ForInstance(instanceFolder),
            string.IsNullOrWhiteSpace(displayName) ? instanceId : displayName);
    }
}
