using BannerlordEnvironmentManager.Core.Install;

namespace BannerlordEnvironmentManager.Core.Nexus;

// Where the exact file a module was installed from stands on its mod page today. This is the question
// a mod page's own version cannot answer: that version tracks the page's main file, and a page carries
// optional files, patches and support files beside it, each on its own numbering. Comparing one of
// those against the page version is how BEM told the user that three files Nexus still lists as
// current were out of date.
public enum InstalledFileStanding
{
    // Nexus's file list for this mod was never read, or BEM holds nothing that says which file it was.
    // Not a statement about the file, and never read as one.
    NotKnown,
    // Nexus published a list and nothing in it is the file BEM recorded. A file Nexus has since taken
    // down comes back this way, and so does a record BEM cannot match to one file with confidence.
    NotListed,
    Current,
    Superseded
}

public sealed record InstalledFileFinding(
    InstalledFileStanding Standing,
    // The file Nexus lists, where BEM found it. Present even when the standing is NotKnown, because a
    // file Nexus lists under a category BEM cannot read is still a file BEM located.
    NexusModFile? Installed = null,
    // What Nexus says replaced it, where Nexus says so. Naming the file to go and get is the
    // difference between a verdict somebody can act on and one they have to research.
    string? ReplacedBy = null,
    DateTimeOffset? ReplacedOnUtc = null)
{
    public static InstalledFileFinding Unknown { get; } = new(InstalledFileStanding.NotKnown);
}

// Compares file to file. Nexus states a category for every file it lists and moves a superseded one
// into its old versions, and it publishes which file replaced which. Those two statements are what
// this reads; nothing here infers anything from a version number.
public static class NexusFileComparison
{
    public static InstalledFileFinding Find(NexusModuleLink link, NexusModFileListing? listing)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (listing is null || listing.Files.Count == 0)
            return InstalledFileFinding.Unknown;

        if (Match(link, listing.Files) is not { } installed)
            // "Nexus lists none of these" and "BEM held nothing to look for" are opposite statements.
            // Reporting the first while BEM had recorded no file at all said the installed copy could
            // not be found on a page BEM had never searched for it on.
            return link.IdentifiesTheInstalledFile
                ? new InstalledFileFinding(InstalledFileStanding.NotListed)
                : InstalledFileFinding.Unknown;

        var replacement = Replacement(installed.FileId, listing);

        if (installed.Standing is NexusFileStanding.OldVersion || replacement is not null)
            return new InstalledFileFinding(InstalledFileStanding.Superseded, installed,
                replacement?.NewFileName, replacement?.UploadedUtc);

        return installed.Standing is NexusFileStanding.Current
            ? new InstalledFileFinding(InstalledFileStanding.Current, installed)
            : new InstalledFileFinding(InstalledFileStanding.NotKnown, installed);
    }

    // Strongest identification first. A file id is Nexus's own handle on one file and settles it; a
    // name has to be unique in the list before it settles anything, because more than one answer is no
    // answer and naming the wrong file is how somebody is told to replace a mod they already have.
    private static NexusModFile? Match(NexusModuleLink link, IReadOnlyList<NexusModFile> files)
    {
        if (link.NexusFileId is { } fileId && files.FirstOrDefault(file => file.FileId == fileId) is { } byId)
            return byId;

        var recorded = link.NexusFileNameAtInstall;

        if (recorded is not { Length: > 0 })
            return null;

        if (Only(files.Where(file => SameName(file.FileName, recorded))) is { } byFileName)
            return byFileName;

        // BEM renames an archive on the way in but keeps the file's own title in the new name, so the
        // title survives where the download filename does not. Nexus lets one page carry several files
        // under the same title at different versions, so the title alone is an answer only when it is
        // unique, and the version is what separates them when it is not.
        if (NexusArchiveName.TryGetModName(recorded) is not { Length: > 0 } title)
            return null;

        var titled = files.Where(file => SameName(file.Name, title)).ToList();

        return Only(titled.Where(file => ModuleUpdateVerdicts.SameVersion(file.Version, link.NexusVersionAtInstall)))
               ?? Only(titled)
               // Last and weakest: the version Nexus gave the file, held against every file on the page
               // rather than only the ones sharing its title. It settles nothing unless exactly one file
               // carries that version, which is what keeps mod 791's two files at 4.3.5 from picking one
               // at random, and it is what identifies a file whose title an author has since rewritten.
               ?? Only(files.Where(file => link.NexusVersionAtInstall is { Length: > 0 }
                                           && ModuleUpdateVerdicts.SameVersion(file.Version, link.NexusVersionAtInstall)));
    }

    // The page's current files, which answer the question even when BEM cannot name the file a module
    // came from. A module declaring exactly the version of a file Nexus still lists as current is not
    // behind that file. Equality only and never ordering: a module's own numbering and a file's are two
    // different people's habits, so a match across both is evidence and a difference is not.
    public static NexusModFile? CurrentFileAtVersion(NexusModFileListing? listing, string? version) =>
        version is { Length: > 0 } && listing is not null
            ? listing.Files.FirstOrDefault(file => file.Standing is NexusFileStanding.Current
                                                   && ModuleUpdateVerdicts.SameVersion(file.Version, version))
            : null;

    // Nexus records a replacement one hop at a time, so a file three releases old points at the file
    // that replaced it and not at the newest one. Following the chain to its end is what makes the
    // name in the verdict the file to actually go and get.
    private static NexusFileReplacement? Replacement(int fileId, NexusModFileListing listing)
    {
        var uploaded = Uploads(listing);

        var seen = new HashSet<int> { fileId };
        var current = fileId;
        NexusFileReplacement? found = null;

        while (listing.Replacements.FirstOrDefault(hop => hop.OldFileId == current && Succeeds(hop, uploaded)) is { } next
               && seen.Add(next.NewFileId))
        {
            found = next;
            current = next.NewFileId;
        }

        return found;
    }

    // Whether a row in Nexus's file_updates is a statement that one file replaced another. A file cannot
    // be replaced by one uploaded before it, so a row saying so is not supersession and is not read as
    // one. This is not a hypothetical: on a real ledger, four of the seven rows naming a file Nexus
    // still lists as current point at an older file it had already archived, one of them five months
    // earlier, and each of those rows was enough to call a mod installed minutes ago out
    // of date. A row BEM cannot date either end of stands, because refusing it would throw away the
    // ordinary case to guard against the rare one.
    private static bool Succeeds(NexusFileReplacement hop, IReadOnlyDictionary<int, DateTimeOffset> uploaded)
    {
        if (!uploaded.TryGetValue(hop.OldFileId, out var before))
            return true;

        var after = uploaded.TryGetValue(hop.NewFileId, out var listed) ? listed : hop.UploadedUtc;

        return after is not { } when || when >= before;
    }

    private static Dictionary<int, DateTimeOffset> Uploads(NexusModFileListing listing)
    {
        var uploads = new Dictionary<int, DateTimeOffset>();

        foreach (var file in listing.Files)
        {
            if (file.UploadedUtc is { } at)
                uploads[file.FileId] = at;
        }

        return uploads;
    }

    private static NexusModFile? Only(IEnumerable<NexusModFile> files)
    {
        var found = files.Take(2).ToList();

        return found.Count == 1 ? found[0] : null;
    }

    // Compared with the extension and without it, because the recorded name may be a download filename
    // and the thing it is being held against may be a file's title.
    private static bool SameName(string? left, string? right) =>
        left is { Length: > 0 }
        && right is { Length: > 0 }
        && (left.Equals(right, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(left)
                .Equals(Path.GetFileNameWithoutExtension(right), StringComparison.OrdinalIgnoreCase));
}
