using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Install;

// InstalledUtc is when the install this claim describes actually happened, where the source of the
// claim knows. Dating a recovered install today would say the copy on disk is newer than it is, and
// the update rules read that date to decide whether a mod's newest file predates it.
public sealed record ArchiveModuleCandidate(
    string ArchiveFileName,
    IReadOnlyList<string> ModuleIds,
    DateTimeOffset? InstalledUtc = null);

public sealed record ModuleArchiveBackfill(
    IReadOnlyList<ModuleArchiveLink> Recovered,
    int Considered,
    int NoModIdInFileName,
    int NotInstalled,
    int Ambiguous,
    int AlreadyRecorded)
{
    public string Describe()
    {
        if (Considered == 0)
            return Strings.Current["Core.Install.ArchiveBackfill.NoneConsidered"];

        var rejected = new List<string>();

        if (NoModIdInFileName > 0)
            rejected.Add(Strings.Current.Plural("Core.Install.ArchiveBackfill.NoModId", NoModIdInFileName));

        if (NotInstalled > 0)
            rejected.Add(Strings.Current.Plural("Core.Install.ArchiveBackfill.NotInstalled", NotInstalled));

        if (Ambiguous > 0)
            rejected.Add(Strings.Current.Plural("Core.Install.ArchiveBackfill.Ambiguous", Ambiguous));

        if (AlreadyRecorded > 0)
            rejected.Add(Strings.Current.Plural("Core.Install.ArchiveBackfill.AlreadyRecorded", AlreadyRecorded));

        var head = Recovered.Count == 0
            ? Strings.Current.Plural("Core.Install.ArchiveBackfill.NoneRecovered", Considered)
            : Strings.Current.Plural("Core.Install.ArchiveBackfill.Recovered", Recovered.Count, Considered);

        return rejected.Count == 0
            ? head
            : Strings.Current.Format("Core.Install.ArchiveBackfill.LeftAlone", head, string.Join(", ", rejected));
    }
}

// Recovering a link after the fact is guessing, and a wrong guess means telling the user a mod is out
// of date by checking a page that belongs to a different mod. So every rule here rejects rather than
// reaches: the archive must name a mod id, the module it claims must actually be installed, and if two
// archives claim the same module while pointing at different pages, that module gets nothing.
public static class ModuleArchiveRecovery
{
    public static ModuleArchiveBackfill Recover(
        IEnumerable<ArchiveModuleCandidate> candidates,
        IReadOnlyCollection<string> installedModuleIds,
        IReadOnlyList<ModuleArchiveLink> alreadyRecorded,
        DateTimeOffset now,
        ModuleArchiveEvidence evidence = ModuleArchiveEvidence.Backfill)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(installedModuleIds);
        ArgumentNullException.ThrowIfNull(alreadyRecorded);

        var installed = new HashSet<string>(installedModuleIds, StringComparer.OrdinalIgnoreCase);
        var known = alreadyRecorded.Select(link => link.ModuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var claims = new Dictionary<string, List<(string Archive, int ModId, DateTimeOffset? At)>>(StringComparer.OrdinalIgnoreCase);

        var considered = 0;
        var noModId = 0;
        var notInstalled = 0;
        var alreadyKnown = 0;

        foreach (var candidate in candidates)
        {
            var modId = NexusArchiveName.TryGetModId(candidate.ArchiveFileName);

            foreach (var moduleId in candidate.ModuleIds.Where(id => !string.IsNullOrWhiteSpace(id)))
            {
                considered++;

                if (modId is not { } id)
                {
                    noModId++;
                    continue;
                }

                if (!installed.Contains(moduleId))
                {
                    notInstalled++;
                    continue;
                }

                if (known.Contains(moduleId))
                {
                    alreadyKnown++;
                    continue;
                }

                if (!claims.TryGetValue(moduleId, out var list))
                    claims[moduleId] = list = [];

                list.Add((candidate.ArchiveFileName, id, candidate.InstalledUtc));
            }
        }

        var recovered = new List<ModuleArchiveLink>();
        var ambiguous = 0;

        foreach (var (moduleId, list) in claims.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (list.Select(claim => claim.ModId).Distinct().Count() > 1)
            {
                ambiguous++;
                continue;
            }

            // The earliest claim, not the latest: where the same mod was installed more than once, an
            // older date can only make BEM less willing to call the copy on disk current, and the
            // opposite mistake is the one that tells the user nothing needs updating when it does.
            recovered.Add(new ModuleArchiveLink(moduleId, list[0].Archive, list[0].At ?? now, evidence));
        }

        return new ModuleArchiveBackfill(recovered, considered, noModId, notInstalled, ambiguous, alreadyKnown);
    }

    // BEM already wrote down which archive replaced which module folder every time an install went over
    // one, so those entries are recoverable evidence rather than inference. The folder is turned back
    // into a module id by reading the manifest that is there now: a folder that has gone away, or whose
    // manifest declares nothing, yields no claim at all.
    public static IReadOnlyList<ArchiveModuleCandidate> FromQuarantine(QuarantineStore store, string modulesFolderPath)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(modulesFolderPath) || !Directory.Exists(modulesFolderPath))
            return [];

        var candidates = new List<ArchiveModuleCandidate>();

        foreach (var item in store.Read())
        {
            if (ReplacedModuleQuarantine.ArchiveFromReason(item.Reason) is not { } archive)
                continue;

            var folder = Path.GetFileName(item.OriginPath.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (string.IsNullOrWhiteSpace(folder))
                continue;

            var manifest = Path.Combine(modulesFolderPath, folder, "SubModule.xml");

            if (!File.Exists(manifest) || SubModuleXmlParser.TryRecoverDeclaredId(manifest) is not { } moduleId)
                continue;

            candidates.Add(new ArchiveModuleCandidate(archive, [moduleId]));
        }

        return candidates;
    }
}
