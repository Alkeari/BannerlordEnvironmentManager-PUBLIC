using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Instances;

// One row of the version list. Instance is null for a version Steam publishes that is not on this
// machine, and for the install BEM has not adopted yet; IsActive is what the next launch runs.
// Variant defaults to GameDlcSet.Empty: a download BEM can offer from Steam's branch catalog is
// always the base game, since the catalog itself carries no DLC concept yet. IsResting defaults to
// false for the same reason: only an instance already on this machine can be the one resting at the
// canonical paths, so nothing Steam merely offers ever carries the marker.
//
// IsAnotherCopy marks a download row whose exact (version, variant) pair is already on this machine:
// picking it fetches a second, independent install rather than the first one. It is never true of a
// row that carries an instance, because such a row is a copy rather than an offer of one.
public sealed record GameVersionEntry(
    ModuleVersion Version,
    string Branch,
    string BuildId,
    InstalledInstance? Instance,
    bool IsActive,
    GameDlcSet Variant = default,
    bool IsResting = false,
    bool IsAnotherCopy = false)
{
    public bool IsInstalled => Instance is not null;
}

// The one list the version dropdown shows: what is already here, then everything else that can be
// had, newest first. Every (version, variant) pair Steam publishes is offered exactly once, whether
// or not the machine already holds it: a user with one v1.4.8 + War Sails may want a second, and
// dropping the row once the first exists is what made a second copy unreachable. A row for a pair
// that is already here is marked IsAnotherCopy so it reads as what it is rather than as a duplicate.
//
// Nothing has to tell that apart from resuming an unfinished download: a download that stopped short
// registered no instance and lives on the Unfinished Downloads list with its own Resume, and a
// finished one has nothing left to fetch. InstanceRegistry.FolderForDownload keeps the two apart on
// disk, sending a resume back into the partial folder and a second copy into its own.
public static class GameVersionList
{
    public static IReadOnlyList<GameVersionEntry> Build(
        IReadOnlyList<GameVersionEntry> known,
        IReadOnlyList<GameVersionOption> offered,
        IReadOnlyList<GameDlcInfo>? knownDlc = null,
        Func<GameDlcInfo, string, bool>? dlcOffersBranch = null)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(offered);

        // No answer to "does this DLC publish this branch" reads as no, not as yes: a caller that
        // has not checked (or could not) must not have every version offered as a DLC variant by
        // default, since most base versions predate any given DLC entirely.
        var offersBranch = dlcOffersBranch ?? ((_, _) => false);

        var entries = known.ToList();

        // What the machine already holds, and what has already been offered, are two different
        // questions and only the second one suppresses a row. Two Steam branches can carry one
        // version, and offering it twice would put two identical lines in the dropdown.
        bool AlreadyHere(ModuleVersion version, GameDlcSet variant) =>
            known.Any(entry => entry.Version == version && entry.Variant == variant);

        var alreadyOffered = new HashSet<(ModuleVersion Version, GameDlcSet Variant)>();

        foreach (var option in offered)
        {
            if (alreadyOffered.Add((option.Version, GameDlcSet.Empty)))
            {
                entries.Add(new GameVersionEntry(
                    option.Version, option.Branch, option.BuildId, null, false, default, false,
                    AlreadyHere(option.Version, GameDlcSet.Empty)));
            }

            // Every DLC BEM knows how to build is offered too, as its own downloadable variant of
            // this same version, but only when that DLC actually publishes a branch matching this
            // base branch's own name: the DLC is its own Steam app with its own branch list, and a
            // branch a base version comes from (e1.7.0, an old v1.4.x point release) is very often
            // one War Sails, or any later DLC, never built for at all. This is the same literal
            // branch-name match DlcCatalog and DlcDownloadStep use to decide whether a download can
            // succeed, so a row the user can see here is always a row that can succeed there.
            foreach (var dlc in knownDlc ?? [])
            {
                if (!offersBranch(dlc, option.Branch))
                    continue;

                var variant = new GameDlcSet([dlc.AppId]);

                if (!alreadyOffered.Add((option.Version, variant)))
                    continue;

                entries.Add(new GameVersionEntry(
                    option.Version, option.Branch, option.BuildId, null, false, variant, false,
                    AlreadyHere(option.Version, variant)));
            }
        }

        return
        [
            .. entries
                .OrderByDescending(entry => GameVersionCatalog.SeriesRank(entry.Version.Type))
                .ThenByDescending(entry => entry.Version)
        ];
    }
}

// The one list, read two ways. Play switches between the copies that are already on this machine, so
// its dropdown is the on-machine half and has nothing in it a download could be started from; the
// Versions page is where a version is downloaded, so it offers exactly the other half, which is
// every version Steam publishes including the ones a copy of which is already here. Both halves are
// cut from a single Build result, which is what stops the two pages disagreeing about what is
// installed.
public static class GameVersionSplit
{
    // An entry backed by an instance is plainly on this machine. So is the install BEM has not
    // adopted yet: that row carries no instance and is still the version the game runs, so counting
    // it as a download would hide the only version a fresh machine has from Play and offer to
    // download what is already sitting on the disk.
    public static bool IsOnThisMachine(GameVersionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.IsInstalled || entry.IsActive;
    }

    public static IReadOnlyList<GameVersionEntry> OnThisMachine(IReadOnlyList<GameVersionEntry> all)
    {
        ArgumentNullException.ThrowIfNull(all);

        return [.. all.Where(IsOnThisMachine)];
    }

    public static IReadOnlyList<GameVersionEntry> Downloadable(IReadOnlyList<GameVersionEntry> all)
    {
        ArgumentNullException.ThrowIfNull(all);

        return [.. all.Where(entry => !IsOnThisMachine(entry))];
    }
}

// The text a row renders for one (version, variant) pair: the version alone for the base game, and
// the version followed by every DLC's own name for a variant, so "v1.5.2 + War Sails" spells out
// what the folder-facing short key abbreviates.
public static class GameVersionLabel
{
    public static string For(ModuleVersion version, GameDlcSet variant) =>
        variant.IsEmpty ? version.ToString() : $"{version} + {string.Join(" + ", variant.DisplayNames)}";
}
