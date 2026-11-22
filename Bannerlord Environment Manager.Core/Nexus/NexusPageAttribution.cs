using BannerlordEnvironmentManager.Core.Install;

namespace BannerlordEnvironmentManager.Core.Nexus;

// What a candidate mod page turned out to be, once the evidence BEM already holds was read against it.
public enum PageStanding
{
    // Nothing here says either way.
    Unexamined,
    // Something on this machine ties this page to this module.
    Corroborated,
    // BEM holds this page's complete file list and every file on it belongs to a different mod. A page
    // that publishes a module publishes a file containing it, so this is a disproof rather than an
    // absence of proof.
    Refuted
}

public sealed record PageCandidate(int NexusModId, NexusIdSource Source, PageStanding Standing, string Reason);

// Which of two disagreeing pages a module actually came from, decided from what is already on disk and
// already paid for. Nothing here contacts Nexus.
//
// It exists because ranking the sources cannot work. On the reference install, BattleOrderTweaks and
// KingdomStrategiesCommand both carry a manifest Url and an archive name that name different pages,
// and the two disagreements resolve in opposite directions, so any fixed precedence is wrong for one
// of them. What separates them is not where the id came from, it is what the evidence says:
//
//   mod 3453's 31 published files are all called AutoResolveRebalanced, and none is BattleOrderTweaks.
//   mod 9048's 15 published files are all called Courier (Messenger), and none is KingdomStrategiesCommand.
//
// Both manifests point at a page that demonstrably does not publish the module in question, and in
// both cases the archive name carries the module's own name and the version the module declares. So
// both resolve, and they resolve without a ranking.
//
// The safety property that matters more than the coverage: this only ever concludes by elimination
// plus corroboration. A page is ruled out only on a complete file list that names some other mod
// throughout, and the survivor is accepted only where something independent ties it to the module. One
// candidate left for want of evidence is not an answer, and comes back unresolved.
public static class NexusPageAttribution
{
    // Below this a page's file list is too small for its naming to be a habit rather than a
    // coincidence, and refuting anything on it would be reading a pattern into two lines.
    private const int LeastFilesToJudgeAPageBy = 3;

    // A shared opening shorter than this is not a mod's name, it is two words that happen to start the
    // same way.
    private const int ShortestTellingCommonName = 5;

    // How much of a page's files have to share that opening before it counts as what the page is
    // called. Authors do post the odd differently-named support file.
    private const double ShareOfFilesThatMustAgree = 0.6;

    public sealed record Result(
        string ModuleId,
        int? ResolvedModId,
        NexusIdSource ResolvedSource,
        IReadOnlyList<PageCandidate> Candidates,
        string Explanation)
    {
        public bool IsResolved => ResolvedModId is not null;
    }

    public static Result Resolve(
        string moduleId,
        string moduleName,
        string? declaredVersion,
        PageCandidate chosen,
        PageCandidate fromArchive,
        string? archiveFileName,
        IReadOnlyDictionary<int, NexusModFileListing>? filesByModId)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(fromArchive);

        var listings = filesByModId ?? new Dictionary<int, NexusModFileListing>();

        var candidates = new[] { chosen, fromArchive }
            .Select(candidate => Judge(candidate, moduleId, moduleName, declaredVersion, archiveFileName, listings))
            .ToList();

        var surviving = candidates.Where(candidate => candidate.Standing is not PageStanding.Refuted).ToList();

        if (surviving.Count != 1)
            return new Result(moduleId, null, NexusIdSource.None, candidates,
                surviving.Count == 0
                    ? "Every page named for this module is contradicted by its own published files, so BEM will not "
                      + "pick one of them."
                    : "Nothing on this machine rules either page out, so BEM will not choose between them.");

        var winner = surviving[0];

        // One candidate left because the other was disproved is not the same as one candidate left that
        // anything actually supports. Accepting on elimination alone would let a page BEM simply holds
        // no file list for win by default, which is the wrongful match this whole thing exists to
        // prevent.
        if (winner.Standing is not PageStanding.Corroborated)
            return new Result(moduleId, null, NexusIdSource.None, candidates,
                $"Mod {candidates.Single(candidate => candidate.NexusModId != winner.NexusModId).NexusModId} is ruled "
                + $"out by its own published files, but nothing here ties this module to mod {winner.NexusModId} "
                + "either, so BEM will not conclude it.");

        var refuted = candidates.Single(candidate => candidate.Standing is PageStanding.Refuted);

        return new Result(moduleId, winner.NexusModId, NexusIdSource.EvidenceResolved, candidates,
            $"Mod {winner.NexusModId} is where this module came from. {winner.Reason} {refuted.Reason}");
    }

    private static PageCandidate Judge(
        PageCandidate candidate,
        string moduleId,
        string moduleName,
        string? declaredVersion,
        string? archiveFileName,
        IReadOnlyDictionary<int, NexusModFileListing> listings)
    {
        if (Corroboration(candidate, moduleId, moduleName, declaredVersion, archiveFileName) is { } supported)
            return candidate with { Standing = PageStanding.Corroborated, Reason = supported };

        if (listings.TryGetValue(candidate.NexusModId, out var listing)
            && Refutation(listing, candidate.NexusModId, moduleId, moduleName) is { } refuted)
            return candidate with { Standing = PageStanding.Refuted, Reason = refuted };

        return candidate with
        {
            Standing = PageStanding.Unexamined,
            Reason = $"Nothing on this machine says whether mod {candidate.NexusModId} publishes this module."
        };
    }

    // Two independent things can tie an archive to the module it installed, and either is enough on its
    // own: Nexus's own filename for a download carries the mod's name, and it carries the version of
    // the file. A module whose own manifest declares that same version, or whose id is in that name, is
    // not a coincidence twice over.
    private static string? Corroboration(
        PageCandidate candidate,
        string moduleId,
        string moduleName,
        string? declaredVersion,
        string? archiveFileName)
    {
        if (candidate.Source is not NexusIdSource.ArchiveFileName
            || NexusArchiveName.TryRead(archiveFileName) is not { } parts
            || parts.ModId != candidate.NexusModId)
            return null;

        var versioned = parts.Version is { Length: > 0 } version
                        && declaredVersion is { Length: > 0 } declared
                        && ModuleUpdateVerdicts.SameVersion(version, declared);

        // A filename with no mod name in it is BEM's own grammar for a download Nexus gave no name for.
        // There the version is all there is, and it is allowed to stand alone.
        if (parts.ModName is not { Length: > 0 } archiveModName)
            return versioned
                ? $"Nexus called the archive it was installed from version {parts.Version}, which is the version the "
                  + "module itself declares."
                : null;

        // A name that names some other mod is evidence against this archive, not an absence of evidence
        // for it, and a matching version cannot rescue it. Version strings as ordinary as 1.0 collide
        // constantly: this rule reached for one and concluded that a module whose manifest named mod
        // 10401 had come from an archive called "Something Else".
        if (!Resembles(archiveModName, moduleId) && !Resembles(archiveModName, moduleName))
            return null;

        return versioned
            ? $"Nexus named the archive it was installed from after this module and at version {parts.Version}, "
              + "which is the version the module itself declares."
            : "Nexus named the archive it was installed from after this module.";
    }

    // A page that publishes a module publishes a file that contains it, and authors name their files
    // after their mod. So a complete file list on which every file is called something else, over and
    // over, is a page that does not publish this module. It is the one negative here strong enough to
    // act on, and it is deliberately hard to satisfy: too few files, or files with no shared name of
    // their own, and this refuses to conclude anything.
    private static string? Refutation(
        NexusModFileListing listing,
        int modId,
        string moduleId,
        string moduleName)
    {
        var names = listing.Files
            .Select(file => file.Name is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(file.FileName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Simplify(name!))
            .Where(name => name.Length > 0)
            .ToList();

        if (names.Count < LeastFilesToJudgeAPageBy)
            return null;

        if (names.Any(name => Resembles(name, moduleId) || Resembles(name, moduleName)))
            return null;

        if (WhatThePageIsCalled(names) is not { } pageName
            || Resembles(pageName, moduleId)
            || Resembles(pageName, moduleName))
            return null;

        return $"Every one of the {listing.Files.Count} files Nexus publishes on mod {modId} is a release of a "
               + "different mod, and none of them is this module.";
    }

    // The opening the page's own files agree on, which is as close as BEM gets to what the page is
    // called without asking Nexus for its name. Null where they agree on nothing worth the name, which
    // is the case that must not refute anything.
    private static string? WhatThePageIsCalled(IReadOnlyList<string> names)
    {
        var needed = (int)Math.Ceiling(names.Count * ShareOfFilesThatMustAgree);

        var longest = names.Max(name => name.Length);

        for (var length = longest; length >= ShortestTellingCommonName; length--)
        {
            var opening = names
                .Where(name => name.Length >= length)
                .GroupBy(name => name[..length], StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() >= needed);

            if (opening is not null)
                return opening.Key;
        }

        return null;
    }

    private static bool Resembles(string? left, string? right) =>
        NexusModNames.Compare(left, right) is NexusNameMatch.Same or NexusNameMatch.Contains;

    private static string Simplify(string name) =>
        new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}
