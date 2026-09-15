using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules;

// What the Play page's detail pane says about a subscribed module, where a Nexus mod gets what Nexus
// reports about it. Not that panel reworded: there is no update verdict to state, because Steam keeps
// the item at the version its author published, and no mod id to show, because the item has none. The
// facts are the ones a subscriber can act on, and the address is where the subscription is ended.
public static class WorkshopModuleFacts
{
    // The install is named where it is known, so a library reached through a junction is read the same
    // way here as everywhere else that asks whether a module is Steam's.
    public static bool Applies(ModuleManifest? module, string? gameInstallPath = null) =>
        !WorkshopContent.NexusSurfaceApplies(module, gameInstallPath);

    // The installed version arrives as text rather than being read off the manifest, because the row
    // it is shown beside already says "Not installed" or "Unreadable" where there is no version to
    // state, and two answers to that question on one screen is one too many.
    public static IReadOnlyList<string> For(
        ModuleManifest? module, string? installedVersion, string? gameInstallPath = null)
    {
        if (!Applies(module, gameInstallPath))
            return [];

        var facts = new List<string>
        {
            ModulePageUrl.WorkshopIdOfFolder(module!.FolderPath) is { } id
                ? Strings.Current.Format("Core.Modules.WorkshopFacts.Item", id)
                : Strings.Current["Core.Modules.WorkshopFacts.ItemUnnamed"]
        };

        if (!string.IsNullOrWhiteSpace(installedVersion))
            facts.Add(Strings.Current.Format("Core.Modules.WorkshopFacts.InstalledVersion", installedVersion));

        facts.Add(Strings.Current["Core.Modules.WorkshopFacts.KeptCurrent"]);
        facts.Add(Strings.Current["Core.Modules.WorkshopFacts.Unsubscribe"]);

        return facts;
    }

    // The same address the refused uninstall offers, since both exist to reach Unsubscribe.
    public static string? Page(ModuleManifest? module, string? gameInstallPath = null) =>
        ModulePageUrl.WorkshopUnsubscribePage(module, gameInstallPath);
}
