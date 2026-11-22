using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

// Every way BEM has of learning which Nexus page a module came from and what version of it is
// installed, and what each one costs. The point of writing them down as a set is that a reader can
// see there is no hidden dependency on this machine, on a premium account, or on having clicked a
// nxm link: the routes that need a key are named, and everything else works without one.
public sealed record UpdateRoute(string Name, string What, bool NeedsApiKey, bool PremiumOnly)
{
    // What and this Describe() are not reached from any Views/ViewModels or XAML today (grepped
    // both), so their text stays a plain literal: nothing shows it to a user yet. Name is migrated
    // separately below because WithoutAKey() does surface it.
    public string Describe() =>
        $"{Name}: {What} {(NeedsApiKey ? "Needs your Nexus API key." : "Needs no key and no account.")}";
}

public static class UpdateRoutes
{
    // Ordered strongest first, which is also the order NexusModuleMatching tries them in. Name is
    // migrated because it reaches the user through WithoutAKey() below; What is not read by anything
    // shipped (only by the unused Describe() above), so it stays a plain literal.
    public static IReadOnlyList<UpdateRoute> All { get; } =
    [
        new(Strings.Current["Core.Nexus.UpdateRoutes.RouteName.Manifest"],
            "The author names their own mod page in the manifest, so it is read straight off disk.",
            NeedsApiKey: false, PremiumOnly: false),
        new(Strings.Current["Core.Nexus.UpdateRoutes.RouteName.Archive"],
            "The mod id in the download's file name, recorded when BEM installs it and kept after the archive is deleted.",
            NeedsApiKey: false, PremiumOnly: false),
        new(Strings.Current["Core.Nexus.UpdateRoutes.RouteName.ButrIndex"],
            "A community index of which Nexus mod publishes which module id, built from Nexus downloads rather than from names.",
            NeedsApiKey: true, PremiumOnly: false),
        new(Strings.Current["Core.Nexus.UpdateRoutes.RouteName.ArchiveHash"],
            "Nexus is asked what a set of bytes is, which gives the mod page, the file and the version Nexus gave it.",
            NeedsApiKey: true, PremiumOnly: false),
        new(Strings.Current["Core.Nexus.UpdateRoutes.RouteName.UpdateFeed"],
            "Which Bannerlord mods have had new files uploaded recently, and what version each is listed at now.",
            NeedsApiKey: true, PremiumOnly: false)
    ];

    // The only thing a premium account changes anywhere in BEM. It is a download route, not an
    // identification or update-check route, so nothing here is worse for a free account.
    public const string PremiumNote =
        "A premium Nexus account changes nothing about update checking. The one place it differs is fetching a file: "
        + "a free account needs the one-time grant that clicking Mod Manager Download mints, which is what BEM already "
        + "uses for everybody.";

    public static string WithoutAKey()
    {
        var free = All.Where(route => !route.NeedsApiKey).Select(route => route.Name);
        var paid = All.Where(route => route.NeedsApiKey).Select(route => route.Name);

        return Strings.Current.Format("Core.Nexus.UpdateRoutes.WithoutAKey", Join(free), Join(paid));
    }

    private static string Join(IEnumerable<string> names)
    {
        var list = names.ToList();

        return list.Count switch
        {
            0 => "nothing",
            1 => list[0],
            _ => $"{string.Join(", ", list[..^1])} and {list[^1]}"
        };
    }
}
