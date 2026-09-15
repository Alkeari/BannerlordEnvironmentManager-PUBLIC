using System.Globalization;
using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

// Four answers, because three of them are true often enough to matter and only one of them is
// "nothing to do". "Cannot tell" is a first-class answer here: presenting it as up to date is the
// false negative that makes an update checker worse than no update checker, because a user who has
// been told everything is current stops looking.
public enum ModuleUpdateState
{
    OutOfDate,
    ProbablyOutOfDate,
    CannotTell,
    UpToDate
}

// What two version strings say about each other, which is a different question from whether they are
// equal. Measured on a real install: 62 of 143 comparable modules had a manifest version that
// differed from the version on the Nexus page, while one mod was actually out of date. Most
// of those differences were not disagreements at all, they were two numbering schemes side by side.
public enum ModuleVersionComparison
{
    // Two numbers that cannot be read against each other. A page whose version is an upload counter
    // against an author's dotted version, a version carrying letters, or nothing at all.
    NotComparable,
    Same,
    Behind,
    Ahead
}

// Whether BEM's own record still describes the copy of the module that is on disk. Every comparison in
// here is a comparison against that record, so a record that has been overtaken cannot support any
// verdict at all: the file it names is not the file installed. This exists because BEM called
// a mod behind on the strength of an install it had logged a month earlier, after that copy had been
// replaced by hand.
public enum InstalledRecordStanding
{
    // Nothing on disk either corroborates or contradicts the record.
    Unconfirmed,
    // What the module declares now is what BEM's record says it should.
    Corroborated,
    // The module declares a later release than the file BEM recorded, so something has been installed
    // over it since and the record no longer describes what is there.
    Contradicted
}

public sealed record ModuleUpdateVerdict(
    string ModuleId,
    string ModuleName,
    ModuleUpdateState State,
    string Reason,
    int? NexusModId = null,
    string? InstalledVersion = null,
    string? NexusVersion = null,
    DateTimeOffset? LatestFileUpdate = null)
{
    public string? ModPageUrl => NexusModId is { } modId ? NexusArchiveName.PageUrl(modId) : null;
}

public sealed record ModuleUpdateTally(int OutOfDate, int ProbablyOutOfDate, int CannotTell, int UpToDate)
{
    public int Total => OutOfDate + ProbablyOutOfDate + CannotTell + UpToDate;

    // The headline a person reads at a glance. Every state that happened is named and no state that
    // did not happen is padded in, so the sentence stays short on a healthy install.
    public string Describe()
    {
        if (Total == 0)
            return Strings.Current["Core.Nexus.UpdateVerdictTally.NoCommunityModules"];

        var parts = new List<string>();

        if (OutOfDate > 0)
            parts.Add(Strings.Current.Format("Core.Nexus.UpdateVerdictTally.OutOfDate", OutOfDate));

        if (ProbablyOutOfDate > 0)
            parts.Add(Strings.Current.Format("Core.Nexus.UpdateVerdictTally.ProbablyOutOfDate", ProbablyOutOfDate));

        if (UpToDate > 0)
            parts.Add(Strings.Current.Format("Core.Nexus.UpdateVerdictTally.UpToDate", UpToDate));

        if (CannotTell > 0)
            parts.Add(Strings.Current.Format("Core.Nexus.UpdateVerdictTally.CannotTell", CannotTell));

        return Strings.Current.Plural("Core.Nexus.UpdateVerdictTally.Summary", Total, string.Join(", ", parts));
    }
}

public static class ModuleUpdateVerdicts
{
    public static ModuleUpdateTally Tally(IEnumerable<ModuleUpdateVerdict> verdicts)
    {
        var byState = verdicts.CountBy(verdict => verdict.State).ToDictionary(pair => pair.Key, pair => pair.Value);

        return new ModuleUpdateTally(
            byState.GetValueOrDefault(ModuleUpdateState.OutOfDate),
            byState.GetValueOrDefault(ModuleUpdateState.ProbablyOutOfDate),
            byState.GetValueOrDefault(ModuleUpdateState.CannotTell),
            byState.GetValueOrDefault(ModuleUpdateState.UpToDate));
    }

    // answered says whether Nexus actually replied. A refusal, an unreachable host or a check that was
    // never run means every module is "cannot tell", never "up to date": those say opposite things and
    // a reader who cannot separate them has been misled rather than informed.
    //
    // lookupByModId says what became of BEM's own request for each mod's current version. It is
    // optional because a caller that never ran a sweep has nothing to say about it, and where it is
    // absent the wording falls back to what the feed alone can support.
    //
    // A module Nexus is the wrong place to ask about is still answered, because "Steam keeps this one
    // current" is a definite answer and not a gap. These used to produce no verdict at all, which left
    // them out of the tally and turned them into a coverage shortfall the user had to read past on
    // every check: 13 modules were reported as load order BEM could not cover when the reason
    // they were not asked about is that there is nothing to ask.
    //
    // filesByModId is the strongest evidence there is, and where it is present it settles the question
    // on its own: Nexus states a category for every file it lists and moves a superseded one into its
    // old versions, so the file installed is either still one of the current files or it is not. The
    // version rules below run only where no such list is held, which is what a run with no key, an
    // older cache or a mod BEM cannot identify a file for all come back as.
    //
    // contentsSinceInstall reads a module's whole folder, so it is only ever called about a module that
    // is otherwise about to be named out of date, which is the single place its answer changes
    // anything. Null means the real check.
    public static IReadOnlyList<ModuleUpdateVerdict> From(
        IEnumerable<NexusModuleLink> links,
        IReadOnlyDictionary<int, NexusUpdatedMod> changedByModId,
        IReadOnlyDictionary<int, string?> nexusVersionByModId,
        NexusUpdatePeriod period,
        bool answered,
        IReadOnlyDictionary<int, NexusVersionLookup>? lookupByModId = null,
        IReadOnlyDictionary<int, NexusModFileListing>? filesByModId = null,
        Func<NexusModuleLink, ModuleContentComparison>? contentsSinceInstall = null)
    {
        var contents = contentsSinceInstall ?? OnDisk;

        return
        [
            .. links.Select(link => For(link, changedByModId, nexusVersionByModId, period, answered, lookupByModId,
                filesByModId, contents))
        ];
    }

    // Which of the two disagreeing sources won, in a form that fits mid-sentence and starts a sentence.
    private static string SourceOf(NexusModuleLink link) => link.Source switch
    {
        NexusIdSource.Manifest => "The Url in its own SubModule.xml",
        NexusIdSource.ManifestMetadata => "Metadata inside its own SubModule.xml",
        NexusIdSource.ArchiveFileName => "The name of the archive it was installed from",
        NexusIdSource.OwnerConfirmed => "The page you accepted for it",
        _ => "What BEM holds for it"
    };

    private static ModuleContentComparison OnDisk(NexusModuleLink link) =>
        InstalledModuleContents.Compare(link.FolderPath ?? string.Empty, link.InstalledFingerprint);

    private static ModuleUpdateVerdict For(
        NexusModuleLink link,
        IReadOnlyDictionary<int, NexusUpdatedMod> changedByModId,
        IReadOnlyDictionary<int, string?> nexusVersionByModId,
        NexusUpdatePeriod period,
        bool answered,
        IReadOnlyDictionary<int, NexusVersionLookup>? lookupByModId,
        IReadOnlyDictionary<int, NexusModFileListing>? filesByModId,
        Func<NexusModuleLink, ModuleContentComparison> contents)
    {
        // Answered before Nexus is consulted at all, because neither answer depends on Nexus and both
        // hold whether or not the check ran, whether or not a key is stored, and whether or not the mod
        // also happens to have a Nexus page.
        if (link.Exclusion is NexusCheckExclusion.SteamWorkshop)
            return Say(link, ModuleUpdateState.UpToDate,
                "Steam keeps a subscribed Workshop item at the version its author has published, so the copy on disk "
                + "is the current one. Nexus was not asked about this module and does not need to be.");

        if (link.Exclusion is NexusCheckExclusion.OwnerSaysNotOnNexus)
            return Say(link, ModuleUpdateState.UpToDate,
                "You marked this module as not published on Nexus, so there is no release there for it to be behind.");

        if (link.Exclusion is NexusCheckExclusion.BuiltLocally)
            return Say(link, ModuleUpdateState.UpToDate,
                "You marked this module as built on this machine, so the copy on disk was never downloaded from Nexus "
                + "and there is no published file it can be behind. Its mod page is still linked and still opens.",
                link.NexusModId);

        if (link.NexusModId is not { } modId)
            return Say(link, ModuleUpdateState.CannotTell,
                "BEM has not found a Nexus mod page for this module, so Nexus could not be asked about it. "
                + "Most mods have one; this says BEM has not identified it, not that the mod has none.");

        // Said before Nexus is consulted, because no answer from Nexus can settle it: BEM would be
        // asking a page it is not sure this module came from. It used to be silent, and silently
        // discarding the losing source's file name and version is what turned two real
        // modules into an unexplained "cannot tell" with nothing on the row to act on.
        if (link.SourcesDisagree)
            return Say(link, ModuleUpdateState.CannotTell,
                $"Two things disagree about which Nexus page this module came from. {SourceOf(link)} names mod "
                + $"{modId}, and the archive BEM recorded installing it names mod {link.ArchiveNexusModId}. Both "
                + "have been wrong before, so BEM will not pick one: open both pages, then use Set Nexus Mod ID on "
                + "this module to say which is right.",
                modId);

        if (!answered)
            return Say(link, ModuleUpdateState.CannotTell,
                "Nexus has not answered about this module, so nothing here is known either way.", modId);

        var changed = changedByModId.GetValueOrDefault(modId);
        var listed = nexusVersionByModId.GetValueOrDefault(modId);

        NexusVersionLookup? lookup = lookupByModId is not null && lookupByModId.TryGetValue(modId, out var outcome)
            ? outcome
            : null;

        // Whether BEM's own record still describes the copy on disk. It gates accusations only, and is
        // deliberately not consulted before anything else: a record something newer was installed over
        // cannot make a module out of date, and where Nexus corroborates that the recorded file is
        // current it cannot make one unknown either. Checking it first cost five real modules
        // a perfectly good "up to date".
        var standing = RecordStanding(link);

        // File against file, which is the only comparison that answers the question that was asked. A
        // mod page's version tracks its main file, so an optional file, a patch or a support file has
        // to be looked up as itself.
        var listing = filesByModId?.GetValueOrDefault(modId);
        var holdsFileList = listing is not null;
        var finding = NexusFileComparison.Find(link, listing);

        // What Nexus still lists as current, held against what the module declares. It is consulted
        // first where BEM's own record has been overtaken, because there the record names a file that
        // is no longer on disk and the module's own declaration is the fresher of the two.
        var atVersion = NexusFileComparison.CurrentFileAtVersion(listing, link.InstalledVersion);

        if (standing is InstalledRecordStanding.Contradicted && atVersion is not null)
            return Say(link, ModuleUpdateState.UpToDate,
                $"Something has been installed over BEM's record of this module since BEM wrote it, and what is there "
                + $"now declares {link.InstalledVersion}, which is the version of a file Nexus still lists as current. "
                + $"{Named(atVersion)}",
                modId, listed, changed);

        if (FromTheFileList(link, finding, standing, contents, modId, listed, changed) is { } fromFiles)
            return fromFiles;

        // BEM cannot name which file this module came from, but Nexus has published the page's files and
        // one of the current ones carries exactly the version the module declares. Two independent
        // sources landing on the same string is not an accident, and it is a far better answer than the
        // mod page's headline version, which tracks only its main file.
        if (atVersion is not null)
            return Say(link, ModuleUpdateState.UpToDate,
                $"This module declares {link.InstalledVersion}, and Nexus lists a file at that version among the current "
                + $"files on its page. {Named(atVersion)}",
                modId, listed, changed);

        // The strongest comparison available, and the only one that compares like with like: what
        // Nexus called the exact file that was installed against what Nexus calls the current one.
        // Both strings come from Nexus, but they come from two different Nexus fields: a file's version
        // and a mod page's version. Measured on a real install, those two disagree for 19 of the
        // 44 archives BEM has a confirmed file version for, and in 17 of the 19 the file installed is
        // the higher of the two. So equality is still proof of being current, and inequality on its own
        // is not proof of anything: it has to be a comparable, lower number, and Nexus has to have
        // recorded a newer file to go with it.
        if (listed is { Length: > 0 } current && link.NexusVersionAtInstall is { Length: > 0 } atInstall)
            // Two strings from the same source, both Nexus's own words for the same thing. Saying they
            // are the same needs no ordering, no scheme and no parsing: it needs them to be the same.
            // Refusing to read "1.3.X" against "1.3.X" because neither is a dotted number was BEM
            // answering a question nobody asked, and it left four real modules unknown on
            // two byte-identical strings.
            return SameVersion(atInstall, current)
                ? Say(link, ModuleUpdateState.UpToDate,
                    $"Nexus called the file you installed {atInstall} and still lists {current} as current.",
                    modId, current, changed)
                : Compare(atInstall, current) switch
            {
                ModuleVersionComparison.Behind when changed is { } newer => Accuse(link, ModuleUpdateState.OutOfDate,
                    $"Nexus called the file you installed {atInstall}, now lists {current}, and recorded a newer file "
                    + $"on {newer.LatestFileUpdate:yyyy-MM-dd}.",
                    standing, contents, modId, current, changed),
                ModuleVersionComparison.Behind => Accuse(link, ModuleUpdateState.ProbablyOutOfDate,
                    $"Nexus called the file you installed {atInstall} and now lists {current}, which is higher. "
                    + $"Nexus has recorded no new file for this mod in {NexusEndpoints.Describe(period)}, so nothing "
                    + "corroborates those two numbers.",
                    standing, contents, modId, current, changed),
                ModuleVersionComparison.Ahead when NothingNewerThanTheCopyInstalled(link, changed) =>
                    Say(link, ModuleUpdateState.UpToDate,
                        $"Nexus called the file you installed {atInstall} and lists {current} for the mod page, which "
                        + "is lower, so there is nothing newer here to get.",
                        modId, current, changed),
                ModuleVersionComparison.Ahead => Say(link, ModuleUpdateState.CannotTell,
                    $"Nexus called the file you installed {atInstall} and lists {current} for the mod page, which is "
                    + "lower, so this page's version is not following its files. Nexus recorded a new file on "
                    + $"{changed!.LatestFileUpdate:yyyy-MM-dd} and those numbers cannot say whether you have it.",
                    modId, current, changed),
                _ => Say(link, ModuleUpdateState.CannotTell,
                    $"Nexus called the file you installed {atInstall} and lists {current} for the mod page. Those are "
                    + "two different numbering schemes, so BEM will not read one against the other. "
                    + WhatTheManifestSays(link)
                    + WhatWouldSettleIt(link, holdsFileList),
                    modId, current, changed)
            };

        // Two different people's version strings, kept for different purposes. A match across two
        // independent sources is unlikely by accident and is taken as current. A mismatch is an
        // observation and never a verdict: this rule used to return "probably out of date" on any
        // difference, which on a real install accused 50 modules of which exactly one was
        // actually behind.
        if (listed is { Length: > 0 } listedVersion && link.InstalledVersion is { Length: > 0 } installed)
            return SameVersion(installed, listedVersion)
                ? Say(link, ModuleUpdateState.UpToDate,
                    $"This module's own SubModule.xml says {installed} and Nexus lists {listedVersion}.",
                    modId, listedVersion, changed)
                : Say(link, ModuleUpdateState.CannotTell,
                    IsGameVersion(installed)
                        ? $"This module's own SubModule.xml says {installed}, which is the Bannerlord build it targets "
                          + $"rather than its own release, and Nexus lists {listedVersion}. There is nothing here to "
                          + "compare. BEM has no record of what version Nexus gave you for this one."
                        : $"This module's own SubModule.xml says {installed} and Nexus lists {listedVersion}. A module's "
                          + "own version and a mod page's version are kept by different people for different purposes, "
                          + "so BEM will not call this out of date on that alone."
                          + WhatWouldSettleIt(link, holdsFileList),
                    modId, listedVersion, changed);

        if (changed is { } update)
            return link.InstalledUtc is { } installedAt && update.LatestFileUpdate <= installedAt
                ? Say(link, ModuleUpdateState.UpToDate,
                    $"The newest file Nexus has recorded for this mod is from {update.LatestFileUpdate:yyyy-MM-dd}, "
                    + $"which is older than the copy BEM installed on {installedAt:yyyy-MM-dd}.",
                    modId, listed, changed)
                : Say(link, ModuleUpdateState.ProbablyOutOfDate,
                    $"Nexus recorded a new file for this mod on {update.LatestFileUpdate:yyyy-MM-dd} and BEM could not "
                    + "read what version it is, so whether it is newer than your copy is unknown.",
                    modId, listed, changed);

        // Nothing above could compare two versions, so the only honest thing left is to say precisely
        // why. Nexus naming no version, BEM's request not coming back, and BEM not having got to this
        // mod yet are three different situations, and a reader who cannot tell them apart does not know
        // whether to wait, to look themselves, or to run the check again.
        //
        // The last case is the false negative this whole thing exists to prevent: Nexus was asked what
        // changed in a window, and a mod that did not change in that window may still have been out of
        // date before it started. Saying "up to date" there would be inventing a fact.
        return lookup switch
        {
            NexusVersionLookup.Listed when listed is { Length: > 0 } onNexus => Say(link, ModuleUpdateState.CannotTell,
                $"Nexus lists {onNexus} as current, and BEM has no record of what version this module is, "
                + "so there is nothing to compare it against.",
                modId, listed),
            NexusVersionLookup.NoVersionListed => Say(link, ModuleUpdateState.CannotTell,
                "Nexus has a page for this mod and states no version on it, so there is nothing to compare the copy "
                + "you have against.",
                modId, listed),
            NexusVersionLookup.LookupFailed => Say(link, ModuleUpdateState.CannotTell,
                "BEM asked Nexus what version this mod is at and got no answer back, so nothing here is known "
                + "either way. It will be asked about again next time.",
                modId, listed),
            NexusVersionLookup.NotAsked => Say(link, ModuleUpdateState.CannotTell,
                "BEM has not asked Nexus what version this mod is at yet. Run the update check again to ask about it.",
                modId, listed),
            _ => Say(link, ModuleUpdateState.CannotTell,
                $"Nexus recorded no new file for this mod in {NexusEndpoints.Describe(period)}, which says nothing about "
                + "whether the copy you have is the current one. BEM has no record of what version Nexus gave you.",
                modId, listed)
        };
    }

    // What Nexus itself says about the exact file, turned into a verdict. Null means Nexus's file list
    // said nothing usable about it, and the version rules below get their turn.
    private static ModuleUpdateVerdict? FromTheFileList(
        NexusModuleLink link,
        InstalledFileFinding finding,
        InstalledRecordStanding standing,
        Func<NexusModuleLink, ModuleContentComparison> contents,
        int modId,
        string? listed,
        NexusUpdatedMod? changed)
    {
        var file = DescribeRecordedFile(link, finding.Installed);

        var replacement = finding.ReplacedBy is { Length: > 0 } named
            ? $" Nexus names {named} as what replaced it"
              + (finding.ReplacedOnUtc is { } on ? $", uploaded on {on:yyyy-MM-dd}." : ".")
            : " There is a newer file on that page to get.";

        switch (finding.Standing)
        {
            case InstalledFileStanding.Superseded:
                return Accuse(link, ModuleUpdateState.OutOfDate,
                    $"Nexus has moved the file you installed, {file}, into its old versions.{replacement}",
                    standing, contents, modId, listed, changed);

            case InstalledFileStanding.Current:
                return Say(link, ModuleUpdateState.UpToDate,
                    $"Nexus still lists the file you installed, {file}, as one of its current files."
                    + (listed is { Length: > 0 } && !SameVersion(listed, finding.Installed?.Version ?? link.NexusVersionAtInstall)
                        ? $" The mod page's own version is {listed}, which tracks its main file and does not describe "
                          + "this one."
                        : string.Empty),
                    modId, listed, changed);

            case InstalledFileStanding.NotListed:
                return Say(link, ModuleUpdateState.CannotTell,
                    $"Nexus published this mod's files and none of them is the one BEM recorded, {file}. Nexus takes a "
                    + "file down as well as superseding one, so this says BEM could not find it rather than that your "
                    + "copy is behind."
                    + (listed is { Length: > 0 } ? $" Nexus lists {listed} for the mod page." : string.Empty),
                    modId, listed, changed);

            // Nexus listed the file and stated a category BEM does not recognize. It located the file,
            // which is more than the version rules can do, and it will not read a category it cannot.
            case InstalledFileStanding.NotKnown when finding.Installed is not null:
                return Say(link, ModuleUpdateState.CannotTell,
                    $"Nexus still lists the file you installed, {file}, and states no category BEM recognizes for it, "
                    + "so BEM cannot say whether it is still one of the current files."
                    + (listed is { Length: > 0 } ? $" Nexus lists {listed} for the mod page." : string.Empty),
                    modId, listed, changed);

            default:
                return null;
        }
    }

    // Every route to "you are behind" goes through here, and nothing else does. An accusation is the
    // one verdict worth confirming before it is made: it is the only one that asks somebody to go and
    // do something, and the only one that is embarrassing when it is wrong.
    //
    // A record something newer has been installed over cannot say a module is behind, because the file
    // it names is not the file on disk. A copy whose files have changed since BEM wrote them can still
    // be behind, but only as far as BEM's last record says, and the wording has to admit it.
    //
    // Nothing is hedged for the absence of evidence. Demoting every module BEM holds no manifest
    // version for would turn a verdict into a shrug on the strength of nothing having been observed,
    // and a checker that shrugs is the one this exists to replace.
    private static ModuleUpdateVerdict Accuse(
        NexusModuleLink link,
        ModuleUpdateState state,
        string observation,
        InstalledRecordStanding standing,
        Func<NexusModuleLink, ModuleContentComparison> contents,
        int modId,
        string? listed,
        NexusUpdatedMod? changed)
    {
        if (standing is InstalledRecordStanding.Contradicted)
            return Say(link, ModuleUpdateState.CannotTell,
                $"{observation} BEM recorded that as the file it installed, and the module on disk declares "
                + $"{link.InstalledVersion}, a later release than that file. Something has been put there since BEM "
                + "wrote its record, so BEM cannot say which file you actually have.",
                modId, listed, changed);

        if (state is not ModuleUpdateState.OutOfDate)
            return Say(link, state, observation, modId, listed, changed);

        // The one place worth reading a module's whole folder, which is why it is read nowhere else.
        return contents(link) is not ModuleContentComparison.Changed
            ? Say(link, ModuleUpdateState.OutOfDate, observation, modId, listed, changed)
            : Say(link, ModuleUpdateState.ProbablyOutOfDate,
                $"{observation} BEM cannot confirm that file is still the copy on disk, so this is what BEM last "
                + "recorded rather than what it has checked.",
                modId, listed, changed);
    }

    // The two free signals that say whether BEM's record still describes what is on disk. Reading the
    // module's files would say it better and costs the whole folder, so that is left to the one caller
    // that needs it.
    public static InstalledRecordStanding RecordStanding(NexusModuleLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (link.InstalledVersion is not { Length: > 0 } declared)
            return InstalledRecordStanding.Unconfirmed;

        // Like for like: what the module declared when BEM recorded the install against what it
        // declares now. Any difference at all means a different copy was put there afterwards.
        if (link.ModuleVersionAtInstall is { Length: > 0 } atInstall)
            return SameVersion(atInstall, declared)
                ? InstalledRecordStanding.Corroborated
                : InstalledRecordStanding.Contradicted;

        // No record of what the module declared, which is every link recovered from BEM's own install
        // log. The file's version is the next best thing to hold it against, and the two are different
        // people's numbering, so only one reading is safe: a module cannot declare a later release than
        // the file it came from unless a later file went in after BEM wrote the record.
        if (link.NexusVersionAtInstall is { Length: > 0 } fileVersion)
            return Compare(declared, fileVersion) switch
            {
                ModuleVersionComparison.Same => InstalledRecordStanding.Corroborated,
                ModuleVersionComparison.Ahead => InstalledRecordStanding.Contradicted,
                _ => InstalledRecordStanding.Unconfirmed
            };

        return InstalledRecordStanding.Unconfirmed;
    }

    // The file as a person would name it: its title and its version, from Nexus where Nexus supplied
    // them and from what BEM wrote down otherwise.
    private static string DescribeRecordedFile(NexusModuleLink link, NexusModFile? file)
    {
        var title = file?.Name ?? NexusArchiveName.TryGetModName(link.NexusFileNameAtInstall);
        var version = file?.Version ?? link.NexusVersionAtInstall;

        return (title, version) switch
        {
            ({ Length: > 0 } named, { Length: > 0 } number) => $"{named} version {number}",
            ({ Length: > 0 } named, _) => named,
            (_, { Length: > 0 } number) => $"version {number}",
            _ => "the file it recorded"
        };
    }

    private static ModuleUpdateVerdict Say(
        NexusModuleLink link,
        ModuleUpdateState state,
        string reason,
        int? modId = null,
        string? nexusVersion = null,
        NexusUpdatedMod? changed = null) =>
        new(link.ModuleId, link.ModuleName, state, reason, modId,
            link.NexusVersionAtInstall ?? link.InstalledVersion, nexusVersion, changed?.LatestFileUpdate);

    // Nexus naming a file newer than the copy on disk is the fact that turns a version difference into
    // an update. Without a date to go on, the honest answer is that something newer may exist.
    private static bool NothingNewerThanTheCopyInstalled(NexusModuleLink link, NexusUpdatedMod? changed) =>
        changed is not { } update || (link.InstalledUtc is { } installedAt && update.LatestFileUpdate <= installedAt);

    // What would settle it, said only where it is not settled already. Telling somebody BEM cannot
    // tell, and stopping there, hands them a problem instead of a next step. The two sentences are
    // different because the two situations are: one is a request BEM has not made yet, the other is a
    // fact about this machine that no request can supply.
    private static string WhatWouldSettleIt(NexusModuleLink link, bool holdsFileList)
    {
        // Two records naming two different mod pages, which is the reason no file on this page matches
        // and no version on it lines up. It outranks everything else that could be said here: until it
        // is settled, every comparison BEM makes is against somebody else's mod.
        if (link.ArchiveNexusModId is { } fromArchive && fromArchive != link.NexusModId)
            return $" BEM is asking about mod page {link.NexusModId}, taken from this module's own SubModule.xml, but "
                   + $"the archive it was installed from names mod page {fromArchive}. One of those is wrong, so every "
                   + "comparison here is against a different mod. Open both pages and tell BEM which one this is.";

        if (holdsFileList)
            return string.Empty;

        return link.IdentifiesTheInstalledFile
            ? " Nexus's own list of this mod's files settles it by comparing file to file, and BEM has not read one "
              + "for this mod yet. Check for updates again to ask Nexus for it."
            // Not a dead end any more. The file list is now fetched for every mod rather than only the
            // ones BEM can name a file for, and the versions of the page's current files answer this
            // without needing to know which file the module arrived in.
            : " BEM also has no record of which file on that page this module came from. Check for updates again to "
              + "read the page's file list, which settles it whenever one of the current files carries the version "
              + "this module declares.";
    }

    // The file as Nexus names it, so a reader can go and look at the one BEM matched rather than take
    // the verdict's word for it.
    private static string Named(NexusModFile file) =>
        file.Name is { Length: > 0 } title
            ? $"Nexus calls it {title}."
            : $"Nexus files it under {file.CategoryName ?? "its current files"}.";

    private static string WhatTheManifestSays(NexusModuleLink link) =>
        link.InstalledVersion is { Length: > 0 } installed
            ? $"This module's own SubModule.xml says {installed}."
            : string.Empty;

    // Authors write v1.2.3, 1.2.3 and 1.2.3.0 for the same release, and a comparison that calls those
    // three different things reports updates that do not exist.
    public static bool SameVersion(string? left, string? right)
    {
        if (Compare(left, right) is ModuleVersionComparison.Same)
            return true;

        var a = Normalize(left);
        var b = Normalize(right);

        if (a.Length == 0 || b.Length == 0)
            return false;

        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
            return true;

        // A trailing zero component carries nothing: e1.4.5 and e1.4.5.0 are one release written two
        // ways, and Components already pads numeric versions so that they compare equal. This is the
        // same courtesy for a version carrying letters, which is the only kind that reaches here.
        //
        // It is withheld where both sides are plain dotted numbers, because "1.0" against "1" is the
        // one pair that must stay unreadable: a mod page whose version is an upload counter reads as a
        // bare number, and calling that the same as a one-point-oh would be exactly the wrong guess.
        return (Components(a) is null || Components(b) is null)
               && WithoutTrailingZeros(a).Equals(WithoutTrailingZeros(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string WithoutTrailingZeros(string version)
    {
        var parts = version.Split('.').ToList();

        while (parts.Count > 1 && parts[^1] == "0")
            parts.RemoveAt(parts.Count - 1);

        return string.Join('.', parts);
    }

    // Bannerlord's own builds are written e1.5.2, and a manifest carrying one of those is stating which
    // game it targets rather than which release of itself this is. Comparing it against anything a mod
    // page says is comparing a mod to the game.
    public static bool IsGameVersion(string? version) =>
        Normalize(version) is { Length: > 1 } text && text[0] is 'e' or 'E' && char.IsAsciiDigit(text[1]);

    // Ahead, behind, the same, or not the same kind of number at all. The last one is the answer this
    // exists for: "3" against "1" is a real comparison, and "1.3" against "2" is a mod's own version
    // against a page that counts its uploads, where reading the lower one as out of date is how BEM
    // accused 69 real modules of being behind when one of them was.
    public static ModuleVersionComparison Compare(string? installed, string? listed)
    {
        var left = Components(installed);
        var right = Components(listed);

        if (left is null || right is null)
            return ModuleVersionComparison.NotComparable;

        // One bare number against a dotted version is two schemes, not two versions. Nexus mod 9715
        // lists "1" while the file installed from it is "3", and mod 11085 lists "2" while
        // its file is "1.3": one of those reads as ahead and the other as behind, and both readings
        // are luck.
        if (left.Count == 1 != (right.Count == 1))
            return ModuleVersionComparison.NotComparable;

        for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            var a = i < left.Count ? left[i] : 0;
            var b = i < right.Count ? right[i] : 0;

            if (a != b)
                return a > b ? ModuleVersionComparison.Ahead : ModuleVersionComparison.Behind;
        }

        return ModuleVersionComparison.Same;
    }

    // Trailing zeros are kept here, unlike in SameVersion, because they carry the shape: 1.0 is a
    // two-part version and 1 is a one-part version, and that difference is the whole scheme test above.
    // Padding makes 1.2.3 and 1.2.3.0 compare equal anyway.
    private static List<int>? Components(string? version)
    {
        var text = Normalize(version);

        if (text.Length == 0)
            return null;

        var parts = text.Split('.');
        var numbers = new List<int>(parts.Length);

        foreach (var part in parts)
        {
            if (part.Length == 0 || !part.All(char.IsAsciiDigit) || !int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                return null;

            numbers.Add(number);
        }

        return numbers;
    }

    private static string Normalize(string? version)
    {
        var text = (version ?? string.Empty).Trim();

        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        return text.Trim();
    }
}
