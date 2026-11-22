using BannerlordEnvironmentManager.Core.Install;

namespace BannerlordEnvironmentManager.Core.Modules;

// Where a module is published, which is not the same question for every module. A Workshop module has
// a page and no Nexus id, and asking Nexus about it would be asking the wrong site: Steam keeps a
// subscribed item current by itself, so it is never behind and never needs checking. It still has a
// page the user may want to open, and BEM already knows its id, because Steam names the folder after
// it.
public static class ModulePageUrl
{
    public static string? WorkshopIdOf(ModuleManifest? module) =>
        module is null || module.Source != ModuleSource.Workshop ? null : WorkshopIdOfFolder(module.FolderPath);

    // The folder alone, for the routes that never see a manifest: a refused uninstall planned from a
    // hand-parsed SubModule.xml, and a leftover copy that arrived as a path and nothing else. Both
    // still owe the user the page the mod is actually removed from.
    public static string? WorkshopIdOfFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return null;

        var folder = Path.GetFileName(
            folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        // Steam names the folder after the published file id and nothing else does, so a folder that is
        // not all digits is not one BEM can build a page address from and it says nothing rather than
        // guessing an address that would open the wrong thing.
        return folder.Length > 0 && folder.All(char.IsAsciiDigit) ? folder : null;
    }

    public static string? WorkshopPage(ModuleManifest? module) => Page(WorkshopIdOf(module));

    // Where a subscription is cancelled: the item's own page, opened in the default web browser rather
    // than the Steam client, because the client shows one Workshop page at a time and a removal of several
    // subscriptions wants several pages open together. The Unsubscribe button there needs the browser to
    // be signed in to Steam, which the control that opens it says. Nothing links past the page, since
    // Steam offers no address that unsubscribes by itself.
    public static string? WorkshopUnsubscribePage(string? folderPath) =>
        WorkshopContent.IsWorkshopPath(folderPath) ? Page(WorkshopIdOfFolder(folderPath)) : null;

    // The same address for a module rather than a bare path. The path test alone is not enough here: a
    // library reached through a junction or a drive substitution carries no steamapps segment, and the
    // Workshop scan has already proved such a module came from the Workshop, so refusing it a page would
    // hide Unsubscribe from the one module that cannot be removed any other way.
    public static string? WorkshopUnsubscribePage(ModuleManifest? module, string? gameInstallPath = null) =>
        module is not null && !WorkshopContent.NexusSurfaceApplies(module, gameInstallPath)
            ? Page(WorkshopIdOfFolder(module.FolderPath))
            : null;

    private static string? Page(string? id) =>
        id is null ? null : $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}";

    // The Nexus page wins where BEM holds an id, because a module can be published in both places and
    // the Nexus id is the one recorded against this install.
    public static string? For(ModuleManifest? module, int? nexusModId) =>
        nexusModId is { } id ? NexusArchiveName.PageUrl(id) : WorkshopPage(module);
}
