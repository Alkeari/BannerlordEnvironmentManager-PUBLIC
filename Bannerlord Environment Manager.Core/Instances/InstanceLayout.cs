namespace BannerlordEnvironmentManager.Core.Instances;

// Instance data lives in the user-visible games root, never inside BEM's own AppData folder, so an
// uninstaller that clears BEM's settings cannot take a save with it.
public static class InstanceLayout
{
    public const string MetadataFileName = "instance.json";
    public const string UserDataFolderName = "UserData";
    public const string GameFolderName = "Game";
    public const string AdoptedBackupFolderName = "adopted-backup";
    public const string OwnGameSettingsFolderName = "own-game-settings";
    public const string ArchiveCacheFolderName = ".archives";
    public const string ToolsFolderName = ".tools";

    public static string MetadataPath(string instanceFolder) =>
        Path.Combine(instanceFolder, MetadataFileName);

    public static string UserDataFolder(string instanceFolder) =>
        Path.Combine(instanceFolder, UserDataFolderName);

    public static string StoreFolder(string instanceFolder, string storeFolderName) =>
        Path.Combine(UserDataFolder(instanceFolder), storeFolderName);

    public static string GameFolder(string instanceFolder) =>
        Path.Combine(instanceFolder, GameFolderName);

    public static string AdoptedBackupFolder(string instanceFolder) =>
        Path.Combine(instanceFolder, AdoptedBackupFolderName);

    // Beside UserData rather than inside it: the frozen copy of the instance's own base game settings
    // is BEM's record of what the player had before sharing, and the folders inside UserData are the
    // ones the game itself writes through a junction while an instance is running.
    public static string OwnGameSettingsFolder(string instanceFolder) =>
        Path.Combine(instanceFolder, OwnGameSettingsFolderName);

    public static string ArchiveCacheFolder(string gamesRoot) =>
        Path.Combine(gamesRoot, ArchiveCacheFolderName);

    public static string ToolsFolder(string gamesRoot) =>
        Path.Combine(gamesRoot, ToolsFolderName);
}
