using System.Text.Json;
using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Nexus;

public enum NexusIdSource
{
    None,
    // The user told BEM which mod page this module is, in so many words. It outranks every derived
    // source because every derived source has now been observed to be wrong: on the reference install
    // one module's manifest Url named the wrong page and a different module's archive name did, and
    // the two errors point in opposite directions. Whatever rule BEM applies to break that tie will be
    // wrong half the time, so the user gets the last word and BEM stops guessing.
    OwnerSet,
    // BEM watched this one happen. The mod id came off the nxm link the mod page handed BEM to download
    // with, or out of Nexus answering about the archive's own hash, so it is a record of where the file
    // actually came from rather than anybody's claim about where it should have come from.
    //
    // It outranks the manifest because it has to. BEM downloaded and installed both of the modules
    // whose pages were reported wrongly, and then read a Url out of the SubModule.xml and believed that
    // instead of what it had just done itself.
    DownloadedByBem,
    // Two sources named two different pages and the evidence already on this machine settled which was
    // right: one page's own published file list proves it publishes some other mod, and something ties
    // the module to the survivor. Ranked with the user's own answer because it is a disproof rather
    // than a preference.
    EvidenceResolved,
    Manifest,
    // The same manifest, said somewhere other than its top-level Url: BUTR's UpdateInfo field, or a
    // NexusId an author put inside a private element of their own.
    ManifestMetadata,
    ArchiveFileName,
    // A page the user was shown and accepted, found in BUTR's index of which Nexus mod publishes
    // which module id. Ranked last because the other two are records of what actually happened on
    // this machine, and this one is an identification the user agreed with.
    OwnerConfirmed
}

// Where an id came from, said in the user's terms. Every value is named: a source that fell through to
// a catch-all read as "nowhere", which was both untrue and worst exactly where it mattered, because the
// value it hid was the one identification nobody had proved.
public static class NexusIdSources
{
    public static string Describe(NexusIdSource source) => source switch
    {
        NexusIdSource.OwnerSet => Strings.Current["Core.Nexus.IdSource.OwnerSet"],
        NexusIdSource.DownloadedByBem => Strings.Current["Core.Nexus.IdSource.DownloadedByBem"],
        NexusIdSource.EvidenceResolved => Strings.Current["Core.Nexus.IdSource.EvidenceResolved"],
        NexusIdSource.Manifest => Strings.Current["Core.Nexus.IdSource.Manifest"],
        NexusIdSource.ManifestMetadata => Strings.Current["Core.Nexus.IdSource.ManifestMetadata"],
        NexusIdSource.ArchiveFileName => Strings.Current["Core.Nexus.IdSource.ArchiveFileName"],
        NexusIdSource.OwnerConfirmed => Strings.Current["Core.Nexus.IdSource.OwnerConfirmed"],
        _ => Strings.Current["Core.Nexus.IdSource.Nowhere"]
    };

    // Whether anything checked this id against what is actually on this machine. An archive name Nexus
    // wrote and a Url the author published are records of what happened; an index match is a resemblance
    // somebody agreed with. Both can be right, and only one of them has been tested.
    public static bool IsProven(NexusIdSource source) => source
        is NexusIdSource.OwnerSet
        or NexusIdSource.DownloadedByBem
        or NexusIdSource.EvidenceResolved
        or NexusIdSource.Manifest
        or NexusIdSource.ManifestMetadata
        or NexusIdSource.ArchiveFileName;

    public static string Caveat(NexusIdSource source) => IsProven(source)
        ? string.Empty
        : Strings.Current["Core.Nexus.IdSource.Caveat"];
}

// Why a module sits outside Nexus update checking altogether. Not a failure to identify it: these are
// modules where Nexus is the wrong place to ask, so asking and reporting "BEM cannot tell" would be
// inventing a worry rather than admitting a gap.
public enum NexusCheckExclusion
{
    None,
    // Steam keeps a subscribed Workshop item current by itself. A Workshop module is therefore never
    // out of date and never unknown, and it stays excluded even where BEM happens to hold a Nexus id
    // for the same mod, because the copy on disk is the Steam one.
    SteamWorkshop,
    // The user said this module is not published on Nexus. Their statement, not BEM's inference.
    OwnerSaysNotOnNexus,
    // The user said the copy on disk was compiled here. The mod page is real and stays linked; what
    // does not exist is a published file to compare it against, because the thing on disk was never
    // published. A development build sits ahead of its own page as a matter of routine.
    BuiltLocally
}

public sealed record NexusModuleLink(
    string ModuleId,
    string ModuleName,
    int? NexusModId,
    NexusIdSource Source,
    // What the module's own SubModule.xml declares. An author's own habit, and only comparable with a
    // Nexus version at the risk of comparing two different people's numbering.
    string? InstalledVersion = null,
    // What Nexus called the exact file that was installed, recorded at install time. Comparable with
    // what Nexus lists today without that risk, which is why it is kept apart.
    string? NexusVersionAtInstall = null,
    DateTimeOffset? InstalledUtc = null,
    NexusCheckExclusion Exclusion = NexusCheckExclusion.None,
    // Which file on that page, not just which page. A mod page carries a main file, optional files and
    // support files side by side, each with its own version, so the page's version answers a question
    // about only one of them.
    int? NexusFileId = null,
    // What the file was called. The title survives BEM renaming the archive on the way in, and it is
    // what identifies the file when no file id was ever recorded.
    string? NexusFileNameAtInstall = null,
    // What the module's own SubModule.xml declared at the moment BEM recorded the install, which is the
    // free way to notice that something has been installed over it since.
    string? ModuleVersionAtInstall = null,
    string? FolderPath = null,
    // A digest of the module's files as BEM left them. Reading it costs the whole module folder, so it
    // is only ever consulted about a module that is otherwise about to be named out of date.
    string? InstalledFingerprint = null,
    // Which mod page the archive BEM recorded says it came from, kept even when that is not the page
    // being asked about. Where the two disagree, one of them is wrong, and saying which two pages
    // disagree is a reason somebody can act on. Every other field here is nulled out on a mismatch
    // precisely so one page's file names are never matched against another page's listing.
    int? ArchiveNexusModId = null)
{
    public bool IsCheckable => NexusModId is not null && Exclusion is NexusCheckExclusion.None;

    public bool IsExcluded => Exclusion is not NexusCheckExclusion.None;

    // Whether BEM holds anything that could pick this module's file out of the list Nexus publishes.
    // Without it, asking for that list would spend a request to learn nothing.
    public bool IdentifiesTheInstalledFile =>
        NexusFileId is not null || NexusFileNameAtInstall is { Length: > 0 };

    // Two sources naming two different mod pages for one module. It used to be silent: the higher
    // ranked source won and every field the other one carried was nulled out, so the module lost the
    // file name, the file id and the version it was installed at, and came back as an unexplained
    // "BEM cannot tell". The disagreement is the answer, and it is the one thing here somebody can act
    // on, so it is said rather than resolved. An id the user set by hand is their answer to exactly
    // this question and settles it.
    public bool SourcesDisagree =>
        Source is not NexusIdSource.OwnerSet
        && ArchiveNexusModId is { } fromArchive
        && NexusModId is { } chosen
        && fromArchive != chosen;

    public string? ModPageUrl => NexusModId is { } modId ? NexusArchiveName.PageUrl(modId) : null;

    public string? ArchiveModPageUrl =>
        ArchiveNexusModId is { } modId ? NexusArchiveName.PageUrl(modId) : null;
}

// The Nexus id is a hint and nothing in BEM depends on it. A module that carries none is not an
// error and not a gap: it is simply outside what update checking can see, and it is counted and
// reported as such rather than quietly dropped.
public static class NexusModuleMatching
{
    // A null map is not "this module came from no archive": it means read back what BEM recorded when
    // it installed the module. That is the whole point of recording it, and defaulting to it here is
    // what lets every caller gain the fallback without each of them having to know the store exists.
    // Pass an empty map, or ModuleArchiveLinkStore.None, to consult nothing.
    public static IReadOnlyList<NexusModuleLink> Link(
        IEnumerable<ModuleManifest> modules,
        IReadOnlyDictionary<string, string>? archiveFileNamesByModuleId = null,
        ModuleArchiveLinkStore? recorded = null,
        LearnedNexusIdStore? confirmed = null,
        NotOnNexusStore? notOnNexus = null,
        BuiltLocallyStore? builtLocally = null,
        // The published file list of every mod page BEM has already asked about. Null means read the
        // copy on this machine, which is what settles a disagreement between two sources without
        // spending a Nexus request. Pass an empty map to consult nothing.
        IReadOnlyDictionary<int, NexusModFileListing>? nexusFilesByModId = null)
    {
        // A caller that supplied its own map has said what to consult, so nothing else is read from
        // disk on its behalf. Only the null map means "read what BEM recorded".
        var store = recorded
                    ?? (archiveFileNamesByModuleId is null ? ModuleArchiveLinkStore.Default : ModuleArchiveLinkStore.None);

        var captured = store.ByModuleId();

        archiveFileNamesByModuleId ??= store.ArchiveFileNamesByModuleId();

        var learned = (confirmed ?? LearnedNexusIdStore.Default);

        var accepted = learned.NexusModIdsByModuleId();

        var setByOwner = learned.NexusModIdsSetByOwner();

        var declaredAbsent = (notOnNexus ?? NotOnNexusStore.Default).ByModuleId();

        var compiledHere = (builtLocally ?? BuiltLocallyStore.Default).ByModuleId();

        var nexusFiles = nexusFilesByModId ?? ReadHeldFileLists();

        var links = new List<NexusModuleLink>();

        foreach (var module in modules)
        {
            // The game's own modules are not on Nexus, so asking about them would spend the user's
            // quota to learn nothing.
            if (module.IsOfficial)
                continue;

            var install = captured.GetValueOrDefault(module.Id.Value);

            var recordedName = install?.ArchiveFileName
                               ?? (archiveFileNamesByModuleId.TryGetValue(module.Id.Value, out var onDisk) ? onDisk : null);

            var fromName = NexusArchiveName.TryRead(recordedName);

            var exclusion = module.Source == ModuleSource.Workshop
                ? NexusCheckExclusion.SteamWorkshop
                : declaredAbsent.ContainsKey(module.Id.Value)
                    ? NexusCheckExclusion.OwnerSaysNotOnNexus
                    : compiledHere.ContainsKey(module.Id.Value)
                        ? NexusCheckExclusion.BuiltLocally
                        : NexusCheckExclusion.None;

            // Whether BEM's record of the archive names a mod page other than the one being asked about.
            // Everything the record holds describes that page's files and no other, so a question about
            // a different page gets none of it.
            bool RecordNamesAnotherPage(int? modId) =>
                install is { RecordedNexusModId: { } recordedMod } && recordedMod != modId;

            // A version out of the archive name is Nexus's own word for that file, so it belongs beside
            // a hash lookup rather than beside the manifest. It is only trusted where the name names the
            // very page being asked about: a mod repackaged from somebody else's archive would otherwise
            // have one page's version compared against another page's listing.
            string? NexusVersionFor(int? modId) =>
                (RecordNamesAnotherPage(modId) ? null : install?.NexusVersionAtInstall)
                ?? (fromName is { ModId: var named, Version: { Length: > 0 } version } && named == modId ? version : null);

            // A file id belongs to one mod page, so it is only offered where the record it came from
            // names the very page being asked about.
            int? NexusFileFor(int? modId) =>
                install is { RecordedNexusModId: { } recordedMod, RecordedNexusFileId: { } recordedFile }
                && recordedMod == modId
                    ? recordedFile
                    : fromName is { ModId: var page, FileId: { } fileId } && page == modId
                        ? fileId
                        : null;

            // Likewise for the file's name: matching one page's file names against another page's
            // listing would name the wrong file, which is worse than naming none.
            string? NexusFileNameFor(int? modId) =>
                RecordNamesAnotherPage(modId) || (fromName is { ModId: var claimed } && claimed != modId)
                    ? null
                    : install?.NexusFileName ?? recordedName;

            NexusModuleLink Link(int? modId, NexusIdSource source) => new(
                module.Id.Value,
                module.Name,
                modId,
                source,
                module.VersionText ?? module.Version.ToString(),
                NexusVersionFor(modId),
                install?.RecordedUtc,
                exclusion,
                NexusFileFor(modId),
                NexusFileNameFor(modId),
                install?.ModuleVersion,
                module.FolderPath,
                install?.InstalledFingerprint,
                install?.RecordedNexusModId ?? fromName?.ModId);

            // The user's own answer, read before anything BEM could derive. It is the only source here
            // that was told to BEM rather than inferred by it, and it is the only way to correct a
            // manifest whose Url points at somebody else's mod page.
            if (setByOwner.TryGetValue(module.Id.Value, out var stated))
            {
                links.Add(Link(stated, NexusIdSource.OwnerSet));
                continue;
            }

            // What BEM itself did, read before anything BEM merely read. This is only ever set by the
            // nxm link a mod page handed BEM to download with, or by Nexus identifying the archive from
            // its own hash, so it is the one derived source that is a record of an event rather than a
            // reading of a string somebody else wrote.
            if (install?.RecordedNexusModId is { } downloaded)
            {
                links.Add(Link(downloaded, NexusIdSource.DownloadedByBem));
                continue;
            }

            // The same fact, said by the name instead of by the field. BEM names a nxm download
            // "nexus-{modId}-{fileId}" when Nexus hands it no name of its own, and that grammar is the
            // one NexusArchiveName treats as a contract precisely because BEM writes it, so an id read
            // out of it records the download BEM performed rather than anybody's claim about the mod.
            // It is deliberate that this is not stored in RecordedNexusModId: ModuleArchiveLink reads
            // ids back out of the name on every load so that a better parser recovers more than the
            // one that ran on the day the file was written. Reading it here is what stops that
            // deliberate choice from silently demoting BEM's own downloads below a manifest Url, which
            // authors copy between mods and leave pointing at the page they forked from.
            if (NexusArchiveName.TryGetOwnDownloadModId(recordedName) is { } named)
            {
                links.Add(Link(named, NexusIdSource.DownloadedByBem));
                continue;
            }

            var fromArchiveName = archiveFileNamesByModuleId.TryGetValue(module.Id.Value, out var fileName)
                ? NexusArchiveName.TryGetModId(fileName)
                : null;

            var fromArchive = install?.NexusModId ?? fromArchiveName;

            var (derived, source) = NexusArchiveName.TryGetModIdFromUrl(module.Url) is { } fromManifest
                ? (fromManifest, NexusIdSource.Manifest)
                : ManifestNexusId.TryRead(module.ManifestPath) is { } fromMetadata
                    ? (fromMetadata, NexusIdSource.ManifestMetadata)
                    : fromArchive is { } fromFileName
                        ? (fromFileName, NexusIdSource.ArchiveFileName)
                        : accepted.TryGetValue(module.Id.Value, out var confirmedModId)
                            ? (confirmedModId, NexusIdSource.OwnerConfirmed)
                            : ((int?)null, NexusIdSource.None);

            // Two sources naming two different pages, settled from the file lists BEM already holds
            // where they settle it. Ranking the sources cannot do this: on the reference install two
            // modules disagree this way and the right answers point in opposite directions. Where the
            // evidence does not decide, nothing is changed and the disagreement is reported instead.
            var recordedArchiveId = install?.RecordedNexusModId ?? fromName?.ModId;

            if (derived is { } chosenId && recordedArchiveId is { } archiveId && chosenId != archiveId)
            {
                var settled = NexusPageAttribution.Resolve(
                    module.Id.Value,
                    module.Name,
                    module.VersionText ?? module.Version.ToString(),
                    new PageCandidate(chosenId, source, PageStanding.Unexamined, string.Empty),
                    new PageCandidate(archiveId, NexusIdSource.ArchiveFileName, PageStanding.Unexamined, string.Empty),
                    install?.ArchiveFileName ?? recordedName,
                    nexusFiles);

                if (settled.ResolvedModId is { } resolved)
                {
                    links.Add(Link(resolved, NexusIdSource.EvidenceResolved));
                    continue;
                }
            }

            links.Add(Link(derived, source));
        }

        return links;
    }

    // A ledger that will not parse is not a ledger with nothing in it, but here it may as well be: the
    // file lists only ever break a tie, and a tie left unbroken is reported rather than guessed at.
    private static IReadOnlyDictionary<int, NexusModFileListing> ReadHeldFileLists()
    {
        try
        {
            return NexusVersionStore.Default.Load().FilesByModId();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new Dictionary<int, NexusModFileListing>();
        }
    }
}
