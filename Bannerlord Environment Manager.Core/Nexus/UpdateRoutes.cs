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

public sealed record UpdateCoverage(
    int Community,
    int Checkable,
    int Manifest,
    int ArchiveFileName,
    int OwnerConfirmed,
    int NexusAnchored,
    int Workshop,
    int NotOnNexus,
    int BuiltLocally,
    int Unidentified,
    int ChangedSinceInstall)
{
    // What a person needs at a glance. Everything else is detail. The two counts are nested on
    // purpose: NexusAnchored is a subset of Checkable, so the sentence can only ever claim "N of
    // those" where N is no larger than the number that can be checked.
    public string Headline() =>
        Community == 0
            ? Strings.Current["Core.Nexus.UpdateCoverage.Headline.NoCommunityModules"]
            : Strings.Current.Plural("Core.Nexus.UpdateCoverage.Headline.Coverage", Community, Checkable, NexusAnchored);

    public string Detail()
    {
        var lines = new List<string>
        {
            Strings.Current.Format("Core.Nexus.UpdateCoverage.Detail.Sources", Manifest, ArchiveFileName, OwnerConfirmed)
        };

        // Almost every Bannerlord mod is published on Nexus and has a mod id. What this number counts
        // is the ones BEM has not found the id for at all, which is a gap in BEM's records rather than
        // a fact about the mods, and saying it the other way round tells the user something untrue.
        if (Unidentified > 0)
            lines.Add(Strings.Current.Format("Core.Nexus.UpdateCoverage.Detail.Unidentified", Unidentified));

        // A Workshop mod has no Nexus page by construction, so counting it as one BEM failed to
        // identify overstates the gap every time the sentence is read.
        if (Workshop > 0)
            lines.Add(Strings.Current.Format("Core.Nexus.UpdateCoverage.Detail.Workshop", Workshop));

        if (NotOnNexus > 0)
            lines.Add(Strings.Current.Plural("Core.Nexus.UpdateCoverage.Detail.NotOnNexus", NotOnNexus));

        if (BuiltLocally > 0)
            lines.Add(Strings.Current.Format("Core.Nexus.UpdateCoverage.Detail.BuiltLocally", BuiltLocally));

        lines.Add(NexusAnchored > 0
            ? Strings.Current.Plural("Core.Nexus.UpdateCoverage.Detail.NexusAnchored.Some", NexusAnchored)
            : Strings.Current["Core.Nexus.UpdateCoverage.Detail.NexusAnchored.None"]);

        if (ChangedSinceInstall > 0)
            lines.Add(Strings.Current.Plural("Core.Nexus.UpdateCoverage.Detail.ChangedSinceInstall", ChangedSinceInstall));

        return string.Join(" ", lines);
    }
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

    public static UpdateCoverage Measure(
        IReadOnlyList<NexusModuleLink> links,
        int changedSinceInstall)
    {
        ArgumentNullException.ThrowIfNull(links);

        return new UpdateCoverage(
            links.Count,
            // "Can be update-checked" is the set of links with a mod id that are not excluded, whatever
            // the id's source. Summing only the manifest/archive/confirmed sources undercounted it:
            // a module identified by the download BEM made itself, or by evidence, or set by the user
            // is exactly as checkable and was dropped from the count. Use IsCheckable, which is the
            // same rule everywhere else in BEM, so this number agrees with the rest of the product.
            links.Count(link => link.IsCheckable),
            links.Count(link => link.Source is NexusIdSource.Manifest or NexusIdSource.ManifestMetadata),
            links.Count(link => link.Source == NexusIdSource.ArchiveFileName),
            links.Count(link => link.Source == NexusIdSource.OwnerConfirmed),
            // A module can only be told "out of date" (rather than "probably") if it is update-checked
            // and carries the version Nexus gave the installed file. Restricting to checkable links is
            // what keeps this a subset of Checkable, so the headline's "N of those" is always honest.
            links.Count(link => link.IsCheckable && link.NexusVersionAtInstall is { Length: > 0 }),
            links.Count(link => link.Exclusion == NexusCheckExclusion.SteamWorkshop),
            links.Count(link => link.Exclusion == NexusCheckExclusion.OwnerSaysNotOnNexus),
            links.Count(link => link.Exclusion == NexusCheckExclusion.BuiltLocally),
            // Genuinely absent ids only. An excluded module (Workshop, not on Nexus, built here) is not
            // an identification gap, and counting it as one repeated the sentence that named it.
            links.Count(link => !link.IsCheckable && link.Exclusion == NexusCheckExclusion.None),
            changedSinceInstall);
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
