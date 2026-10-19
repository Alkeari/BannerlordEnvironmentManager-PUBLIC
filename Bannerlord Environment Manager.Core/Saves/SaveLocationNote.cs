using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Saves;

// Why an empty save list can mean "BEM could not look" rather than "you have no saves".
//
// Read out of the game's own assemblies: TaleWorlds.Core.FilePaths.SavePath is
// PlatformDirectoryPath(PlatformFileType.User, "Game Saves"), so the real folder is whatever
// Common.PlatformFileHelper turns PlatformFileType.User into. TaleWorlds.Library.PlatformFileHelperPC
// maps User to Documents\<application name>, which is the folder SaveReader reads.
//
// On Game Pass the helper installed at startup is the GDK one, which stores through Xbox rather than in
// Documents. Read out of Bannerlord.BLSE.Shared.dll: XboxFeature.Enable runs only when the working
// directory is Gaming.Desktop.x64_Shipping_Client, and ModulePatch then prefixes
// TaleWorlds.MountAndBlade.Platform.GDK.PlatformGDKSubModule.OnSubModuleLoad with a prefix that sets
// Common.PlatformFileHelper = new PlatformFileHelperPC("Mount and Blade II Bannerlord") and returns
// false, so the GDK original never runs. That patch is the whole reason Documents holds saves on Game
// Pass at all, and it only exists once BLSE is installed.
//
// So on a Game Pass install with no BLSE there is nothing in the Documents folder to find, and saying
// "no saves" there would be a false statement about the campaigns the user actually has.
//
// BEM now reads that folder through InstanceDataRoot rather than building the path here, so while a
// launch is in progress the folder in play is the active instance's own store, not always the machine
// default.
public static class SaveLocationNote
{
    public const string BlseExecutable = "Bannerlord.BLSE.Standalone.exe";

    public static string For(string? gameInstallPath, bool anySavesFound = false)
    {
        var detection = GameInstallLocator.DetectPlatform(gameInstallPath);

        if (!detection.IsGamePass || detection.PlatformFolder is not { } folder)
            return string.Empty;

        if (HasBlse(gameInstallPath!, folder))
            return string.Empty;

        var note = Strings.Current["Core.Saves.LocationNote.GamePassNoBlse"];

        note += anySavesFound
            ? Strings.Current["Core.Saves.LocationNote.SomeSavesFound"]
            : Strings.Current["Core.Saves.LocationNote.NoSavesFound"];

        return note + Strings.Current["Core.Saves.LocationNote.InstallBlse"];
    }

    public static bool HasBlse(string gameInstallPath, string platformFolder)
    {
        if (string.IsNullOrWhiteSpace(gameInstallPath) || string.IsNullOrWhiteSpace(platformFolder))
            return false;

        try
        {
            return File.Exists(Path.Combine(gameInstallPath, "bin", platformFolder, BlseExecutable));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
