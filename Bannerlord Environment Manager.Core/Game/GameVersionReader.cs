using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Game;

public static class GameVersionReader
{
    // Native's SubModule.xml tracks the game build. The executable file version lags behind patches.
    public static ModuleVersion Read(string gameInstallPath)
    {
        if (string.IsNullOrWhiteSpace(gameInstallPath))
            return ModuleVersion.Empty;

        var manifestPath = Path.Combine(
            ModuleScanner.GetModulesFolder(gameInstallPath), "Native", "SubModule.xml");

        if (!File.Exists(manifestPath))
            return ModuleVersion.Empty;

        return SubModuleXmlParser.TryLoad(manifestPath, out var manifest, out _)
            ? manifest.Version
            : ModuleVersion.Empty;
    }
}
