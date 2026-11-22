using System.Xml.Linq;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.LoadOrder;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public sealed record NavalDlcVersionMismatch(ModuleId ModuleId, string DisplayName, ModuleVersion Required, ModuleVersion Game);

// NavalDLC names the game it was built for in <RequiredBaseVersion>. TaleWorlds.ModuleManager ModuleHelper.cs
// InitializeModules compares it with ApplicationVersion.FromParametersFile() for every physical module whose
// <Name> is NavalDLC (item.Name, never the Id), and when they differ shows "NavalDLC version is not matched with base game!" and calls
// Environment.Exit(0): no crash report, the game just ends. ApplicationVersion's == compares type, major,
// minor and revision and ignores the changeset. (IsModuleVersionMismatched, which would skip the DLC instead,
// has no caller in that file.)
//
// The game's own version comes from the Parameters\Version.xml the game bakes into TaleWorlds.Library
// (VirtualFolders.cs), which BEM cannot read; the Version.xml beside the executables carries the same
// Singleplayer value, and Native's manifest is the fallback. A version BEM cannot read proves nothing, so it
// reports nothing.
//
// Caveat: GetPhysicalModules skips NavalDLC when the platform says War Sails is not owned, and then no
// exit happens. CheckEntitlement is a platform call (SteamModuleExtension: BIsSubscribedApp and
// BIsDlcInstalled; consoles always true) and no file in the module tells an owned copy from an unowned one,
// so a present copy is still reported and the detail says it applies only when War Sails is owned.
public static class NavalDlcVersion
{
    public static IReadOnlyList<NavalDlcVersionMismatch> Find(
        IReadOnlyList<ModuleEntry> loadOrder, string? gameInstallPath, string platformFolder)
    {
        ArgumentNullException.ThrowIfNull(loadOrder);

        if (string.IsNullOrWhiteSpace(gameInstallPath))
            return [];

        var game = GameVersion(gameInstallPath, platformFolder);

        if (game == ModuleVersion.Empty)
            return [];

        var found = new List<NavalDlcVersionMismatch>();

        foreach (var entry in loadOrder)
        {
            if (entry.Manifest is not { FolderPath.Length: > 0 } manifest
                || !string.Equals(manifest.Name, "NavalDLC", StringComparison.Ordinal)
                || RequiredBase(Path.Combine(manifest.FolderPath, "SubModule.xml")) is not { } required)
                continue;

            if (!Same(required, game))
                found.Add(new NavalDlcVersionMismatch(entry.Id, entry.DisplayName, required, game));
        }

        return found;
    }

    private static bool Same(ModuleVersion a, ModuleVersion b) =>
        a.Type == b.Type && a.Major == b.Major && a.Minor == b.Minor && a.Revision == b.Revision;

    private static ModuleVersion GameVersion(string gameInstallPath, string platformFolder)
    {
        try
        {
            var path = Path.Combine(gameInstallPath, "bin", platformFolder, "Version.xml");

            if (File.Exists(path)
                && XDocument.Load(path).Root?.Element("Singleplayer")?.Attribute("Value")?.Value is { } text
                && ModuleVersion.TryParse(text, out var version))
                return version;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
        }

        return GameVersionReader.Read(gameInstallPath);
    }

    private static ModuleVersion? RequiredBase(string manifestPath)
    {
        try
        {
            var element = XDocument.Load(manifestPath).Root?.Element("RequiredBaseVersion");

            // ModuleInfo.cs leaves RequiredBaseVersion at default(ApplicationVersion) when the element is
            // absent, and InitializeModules compares that with no Empty guard, so the game still exits.
            if (element is null)
                return new ModuleVersion(ModuleVersionType.Release, 0, 0, 0, 0);

            // An unparsable value throws inside LoadWithFullPath; GetPhysicalModules catches it and skips
            // the module, so no exit follows.
            return element.Attribute("value")?.Value is { } text && ModuleVersion.TryParse(text, out var version)
                ? version
                : null;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
