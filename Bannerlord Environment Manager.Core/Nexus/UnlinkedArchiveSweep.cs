using BannerlordEnvironmentManager.Core.Install;

namespace BannerlordEnvironmentManager.Core.Nexus;

// An archive sitting in the watched folder that BEM has never tied to a Nexus page, together with the
// module ids it installs.
public sealed record SweepCandidate(string ArchivePath, string FileName, IReadOnlyList<string> ModuleIds);

// Identifying a mod BEM did not install itself. The archive is still the only thing that answers the
// question with certainty: Nexus keys its one content lookup on the md5 of the file it published, so a
// download still sitting in the watched folder identifies its own mod page exactly, whatever it has
// been renamed to and whoever downloaded it.
//
// What makes the answer usable is that the archive also states which modules it installs, in the
// SubModule.xml inside it. Nexus says which page the file came from and the file says which module it
// becomes, so the pair is established by evidence at both ends rather than by matching names.
public static class UnlinkedArchiveSweep
{
    // An archive is worth hashing when none of the modules it installs already has a Nexus id. Asking
    // again about something already identified spends a request from an allowance that is the user's
    // and is shared with whatever else they run.
    public static IReadOnlyList<SweepCandidate> Worth(
        IEnumerable<SweepCandidate> archives,
        IEnumerable<ModuleArchiveLink> recorded)
    {
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentNullException.ThrowIfNull(recorded);

        var identified = recorded
            .Where(link => link.NexusModId is not null)
            .Select(link => link.ModuleId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. archives
                .Where(archive => archive.ModuleIds.Count > 0)
                .Where(archive => !archive.ModuleIds.All(identified.Contains))
                .DistinctBy(archive => archive.ArchivePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(archive => archive.FileName, StringComparer.OrdinalIgnoreCase)
        ];
    }

    // One link per module the archive installs. A single archive commonly carries a library and the mod
    // that needs it, and both came from the same page, so both are tied to it rather than only the first.
    public static IReadOnlyList<ModuleArchiveLink> LinksFor(
        SweepCandidate candidate,
        string md5,
        DateTimeOffset observedUtc)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return
        [
            .. candidate.ModuleIds.Select(moduleId => new ModuleArchiveLink(
                moduleId,
                candidate.FileName,
                observedUtc,
                ModuleArchiveEvidence.Install,
                ArchiveMd5: md5))
        ];
    }
}
