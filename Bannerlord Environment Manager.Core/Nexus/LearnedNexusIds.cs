using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

public enum NexusIdProposalBasis
{
    // BEM downloaded this file from that mod page itself, and BUTR's index says that page publishes
    // exactly this installed module. The strongest thing that is still not a manifest.
    DownloadedArchive,
    // One mod page in the whole Bannerlord library on Nexus publishes a module with this id.
    ButrSoleMod,
    // Several do. Which one this copy came from is not knowable from here.
    ButrOneOfSeveral
}

// How well the name Nexus gives a mod page matches the name the installed module gives itself. It
// ranks candidates and it is written into the explanation, and it ticks nothing: two mods can share a
// name and an author can rename a page, so a name is evidence to put in front of the user and never
// grounds for BEM to decide on its own.
public enum NexusNameMatch
{
    Unknown,
    Different,
    Contains,
    Same
}

// A mod id BEM believes belongs to a module, with the reason written next to it. Nothing here is
// recorded anywhere until the user accepts it: a wrong id says a mod is out of date when it is
// not, and that is worse than saying nothing.
public sealed record NexusIdProposal(
    string ModuleId,
    string ModuleName,
    int NexusModId,
    NexusIdProposalBasis Basis,
    string Explanation,
    bool SuggestedByDefault,
    // What Nexus calls that page. Null means BEM has not asked Nexus about this page, which is not the
    // same as Nexus having no name for it.
    string? NexusModName = null,
    NexusNameMatch NameMatch = NexusNameMatch.Unknown)
{
    public string ModPageUrl => Install.NexusArchiveName.PageUrl(NexusModId);

    // A bare mod id asks the user to judge a number. The page's own name is what makes it recognizable
    // at a glance, so it goes in the line itself rather than behind a link.
    public string Headline =>
        NexusModName is { Length: > 0 } name
            ? Strings.Current.Format("Core.Nexus.IdProposal.Headline.Named", ModuleName, ModuleId, NexusModId, name)
            : Strings.Current.Format("Core.Nexus.IdProposal.Headline.Unnamed", ModuleName, ModuleId, NexusModId);
}

public static class NexusModNames
{
    public static NexusNameMatch Compare(string? moduleName, string? nexusModName)
    {
        var module = Simplify(moduleName);
        var nexus = Simplify(nexusModName);

        if (module.Length == 0 || nexus.Length == 0)
            return NexusNameMatch.Unknown;

        if (module.Equals(nexus, StringComparison.Ordinal))
            return NexusNameMatch.Same;

        return module.Contains(nexus, StringComparison.Ordinal) || nexus.Contains(module, StringComparison.Ordinal)
            ? NexusNameMatch.Contains
            : NexusNameMatch.Different;
    }

    public static string Describe(NexusNameMatch match, string nexusModName, int modId) => match switch
    {
        NexusNameMatch.Same => Strings.Current.Format("Core.Nexus.NameMatch.Same", modId, nexusModName),
        NexusNameMatch.Contains => Strings.Current.Format("Core.Nexus.NameMatch.Contains", modId, nexusModName),
        NexusNameMatch.Different => Strings.Current.Format("Core.Nexus.NameMatch.Different", modId, nexusModName),
        _ => string.Empty
    };

    // Authors write "RTS Camera", "RTSCamera" and "RTS_Camera" for one mod, and a comparison that calls
    // those three different things makes every real match look like a coincidence.
    private static string Simplify(string? name) =>
        new([.. (name ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}

public sealed record NexusIdProposalSet(
    IReadOnlyList<NexusIdProposal> Proposals,
    IReadOnlyList<string> UnknownModuleIds,
    IReadOnlyList<int> UnattributedModIds)
{
    public static NexusIdProposalSet Empty { get; } = new([], [], []);

    public int Modules => Proposals.Select(proposal => proposal.ModuleId)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public int Suggested => Proposals.Count(proposal => proposal.SuggestedByDefault);

    public string Describe()
    {
        if (Proposals.Count == 0 && UnknownModuleIds.Count == 0)
            return Strings.Current["Core.Nexus.IdProposalSet.EveryModuleAlreadyIdentified"];

        var found = Proposals.Count == 0
            ? Strings.Current["Core.Nexus.IdProposalSet.NoneNamed"]
            : Strings.Current.Format("Core.Nexus.IdProposalSet.NamedFor", Modules, Suggested);

        var unknown = UnknownModuleIds.Count > 0
            ? " " + Strings.Current.Plural("Core.Nexus.IdProposalSet.NotInIndex", UnknownModuleIds.Count)
            : string.Empty;

        var loose = UnattributedModIds.Count > 0
            ? " " + Strings.Current.Plural("Core.Nexus.IdProposalSet.Unattributed", UnattributedModIds.Count)
            : string.Empty;

        return found + unknown + loose + " " + Strings.Current["Core.Nexus.IdProposalSet.NothingRecordedYet"];
    }
}

public static class NexusIdProposals
{
    // Links come in from NexusModuleMatching, so a module that already has an id from its own
    // manifest, from the archive it was installed from, or from a previous confirmation is never
    // proposed a second one.
    // modNamesByModId is what Nexus calls each candidate page, where BEM has asked. It changes no
    // decision: it orders the candidates for a module so the likeliest is read first, and it puts the
    // page's name in front of the user so a choice between two ids is a choice between two names.
    public static NexusIdProposalSet From(
        IReadOnlyList<NexusModuleLink> links,
        IReadOnlyDictionary<string, IReadOnlyList<int>> nexusModIdsByModuleId,
        IReadOnlyDictionary<int, IReadOnlyList<string>> moduleIdsByNexusModId,
        IReadOnlyList<int> downloadedModIdsWithNoModule,
        IReadOnlyDictionary<int, string>? modNamesByModId = null)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(nexusModIdsByModuleId);
        ArgumentNullException.ThrowIfNull(moduleIdsByNexusModId);
        ArgumentNullException.ThrowIfNull(downloadedModIdsWithNoModule);

        var names = modNamesByModId ?? new Dictionary<int, string>();

        NexusIdProposal Named(NexusIdProposal proposal)
        {
            if (!names.TryGetValue(proposal.NexusModId, out var name) || string.IsNullOrWhiteSpace(name))
                return proposal;

            var match = NexusModNames.Compare(proposal.ModuleName, name);

            return proposal with
            {
                NexusModName = name,
                NameMatch = match,
                Explanation = $"{proposal.Explanation} {NexusModNames.Describe(match, name, proposal.NexusModId)}".Trim()
            };
        }

        // A module deliberately outside Nexus checking is not a module waiting to be identified. Asking
        // the user to pick a mod page for their own unpublished mod, or for a Workshop subscription
        // Steam already keeps current, is work they cannot finish.
        var uncovered = links
            .Where(link => link.NexusModId is null && !link.IsExcluded)
            .GroupBy(link => link.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var proposals = new List<NexusIdProposal>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unattributed = new List<int>();

        foreach (var modId in downloadedModIdsWithNoModule.Distinct().OrderBy(id => id))
        {
            var candidates = moduleIdsByNexusModId.TryGetValue(modId, out var moduleIds)
                ? moduleIds.Where(uncovered.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];

            if (candidates.Count == 0)
            {
                unattributed.Add(modId);
                continue;
            }

            foreach (var moduleId in candidates)
            {
                var module = uncovered[moduleId];

                proposals.Add(Named(new NexusIdProposal(
                    module.ModuleId,
                    module.ModuleName,
                    modId,
                    NexusIdProposalBasis.DownloadedArchive,
                    candidates.Count == 1
                        ? Strings.Current.Format("Core.Nexus.IdProposal.Basis.DownloadedArchiveSingle", modId)
                        : Strings.Current.Plural("Core.Nexus.IdProposal.Basis.DownloadedArchiveMultiple", candidates.Count, modId),
                    candidates.Count == 1)));

                claimed.Add(moduleId);
            }
        }

        var unknown = new List<string>();

        foreach (var (moduleId, module) in uncovered.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (claimed.Contains(moduleId))
                continue;

            if (!nexusModIdsByModuleId.TryGetValue(moduleId, out var modIds) || modIds.Count == 0)
            {
                unknown.Add(moduleId);
                continue;
            }

            var forModule = modIds.Distinct().OrderBy(id => id)
                .Select(modId => Named(new NexusIdProposal(
                    module.ModuleId,
                    module.ModuleName,
                    modId,
                    modIds.Count == 1 ? NexusIdProposalBasis.ButrSoleMod : NexusIdProposalBasis.ButrOneOfSeveral,
                    modIds.Count == 1
                        ? Strings.Current["Core.Nexus.IdProposal.Basis.ButrSoleMod"]
                        : Strings.Current.Plural("Core.Nexus.IdProposal.Basis.ButrOneOfSeveral", modIds.Count),
                    modIds.Count == 1)));

            // Where several pages publish the same module id, the one whose name matches is read first.
            // None of them is ticked by that: ordering is a courtesy, and ticking would be a decision.
            proposals.AddRange(forModule
                .OrderByDescending(proposal => proposal.NameMatch)
                .ThenBy(proposal => proposal.NexusModId));
        }

        return new NexusIdProposalSet(proposals, unknown, unattributed);
    }
}

// Every entry here was accepted by the user, and the sentence that persuaded them is kept beside
// it so a link can be argued with later rather than merely trusted.
// SetByOwner separates the two ways an entry gets here, which are not the same statement. Accepting a
// candidate BEM proposed says "that one looks right"; typing a mod id against a module that already
// had one says "the thing you derived is wrong, here is the answer". Only the second may overrule a
// manifest, so only the second is read before one. It defaults to false, which is what every entry
// written before this existed was.
public sealed record LearnedNexusId(
    string ModuleId,
    int NexusModId,
    string Basis,
    DateTimeOffset RecordedUtc,
    bool SetByOwner = false);

// Failed is what the file would have held if the write had worked. It is counted apart from Refused
// because "BEM would not write this" and "BEM could not write this" are different answers, and both
// used to arrive as the same silent success.
public sealed record LearnedNexusIdRecord(int Added, int Replaced, int Unchanged, int Refused, int Failed = 0)
{
    public int Written => Added + Replaced;

    // Forget reports its removals in the Replaced slot, so anything reading Added here would say
    // nothing happened every single time it worked.
    public string DescribeForget(string moduleId) => this switch
    {
        { Failed: > 0 } => Strings.Current.Format("Core.Nexus.LearnedIds.DescribeForget.Failed", moduleId),
        { Replaced: > 0 } => Strings.Current.Format("Core.Nexus.LearnedIds.DescribeForget.Replaced", moduleId),
        _ => Strings.Current.Format("Core.Nexus.LearnedIds.DescribeForget.NothingToForget", moduleId)
    };
}

public sealed class LearnedNexusIdStore
{
    public const string FileName = "confirmed-nexus-ids.json";

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public LearnedNexusIdStore(string? filePath) => FilePath = filePath;

    public static LearnedNexusIdStore Default { get; } = new(DefaultPath());

    public static LearnedNexusIdStore None { get; } = new((string?)null);

    public string? FilePath { get; }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        FileName);

    public IReadOnlyList<LearnedNexusId> Load()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            return [];

        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<LearnedNexusId>>(File.ReadAllText(FilePath), Format) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    public IReadOnlyDictionary<string, int> NexusModIdsByModuleId()
    {
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var learned in Load().OrderBy(learned => learned.RecordedUtc))
            ids[learned.ModuleId] = learned.NexusModId;

        return ids;
    }

    // Only what the user stated outright, which is the subset allowed to overrule a manifest.
    public IReadOnlyDictionary<string, int> NexusModIdsSetByOwner()
    {
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var learned in Load().Where(learned => learned.SetByOwner).OrderBy(learned => learned.RecordedUtc))
            ids[learned.ModuleId] = learned.NexusModId;

        return ids;
    }

    // A module the user ticked twice, once per candidate page, is refused rather than resolved to
    // whichever came first. Two answers to "which page is this" means BEM does not know.
    public LearnedNexusIdRecord Record(IEnumerable<LearnedNexusId> learned)
    {
        ArgumentNullException.ThrowIfNull(learned);

        var incoming = learned.ToList();

        if (string.IsNullOrWhiteSpace(FilePath) || incoming.Count == 0)
            return new LearnedNexusIdRecord(0, 0, 0, 0);

        var contested = incoming
            .GroupBy(entry => entry.ModuleId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(entry => entry.NexusModId).Distinct().Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existing = Load().ToDictionary(entry => entry.ModuleId, StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var replaced = 0;
        var unchanged = 0;

        foreach (var entry in incoming)
        {
            if (contested.Contains(entry.ModuleId))
                continue;

            if (!existing.TryGetValue(entry.ModuleId, out var already))
            {
                existing[entry.ModuleId] = entry;
                added++;
                continue;
            }

            if (already.NexusModId == entry.NexusModId)
            {
                unchanged++;
                continue;
            }

            existing[entry.ModuleId] = entry;
            replaced++;
        }

        if (added + replaced > 0
            && !Write([.. existing.Values.OrderBy(entry => entry.ModuleId, StringComparer.OrdinalIgnoreCase)]))
            return new LearnedNexusIdRecord(0, 0, 0, contested.Count, added + replaced);

        return new LearnedNexusIdRecord(added, replaced, unchanged, contested.Count);
    }

    // A copy of the file as it stands, made before anything throws it away. Returns where it was put,
    // or null if there was nothing to copy. Rebuilding every id from scratch is the one action here
    // that can lose an answer nobody can work out again, so it does not happen without this.
    public string? Backup(DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
            return null;

        try
        {
            var path = Path.Combine(
                Path.GetDirectoryName(FilePath)!,
                $"{Path.GetFileNameWithoutExtension(FilePath)} {now.ToLocalTime().ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)}.bak.json");

            File.Copy(FilePath, path, overwrite: true);

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    // Everything, in one go, for a rebuild from scratch. Reported through Replaced like Forget, because
    // it is the same operation over every module rather than a different one.
    public LearnedNexusIdRecord Clear() => Forget(Load().Select(entry => entry.ModuleId));

    // The other direction, which is what makes the rebuild safe to press. A backup that cannot be put
    // back is a copy, not a safety net.
    public bool Restore(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(FilePath) || string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            return false;

        try
        {
            // Read it as a ledger first: a file that will not parse must not be moved over the live one.
            JsonSerializer.Deserialize<List<LearnedNexusId>>(File.ReadAllText(backupPath), Format);

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.Copy(backupPath, FilePath, overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public LearnedNexusIdRecord Forget(IEnumerable<string> moduleIds)
    {
        ArgumentNullException.ThrowIfNull(moduleIds);

        var drop = moduleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(FilePath) || drop.Count == 0)
            return new LearnedNexusIdRecord(0, 0, 0, 0);

        var kept = Load().Where(entry => !drop.Contains(entry.ModuleId)).ToList();
        var removed = Load().Count - kept.Count;

        if (removed > 0 && !Write([.. kept.OrderBy(entry => entry.ModuleId, StringComparer.OrdinalIgnoreCase)]))
            return new LearnedNexusIdRecord(0, 0, 0, 0, removed);

        return new LearnedNexusIdRecord(0, removed, 0, 0);
    }

    // Written beside the real file and moved over it, so a write that stops halfway leaves every id
    // already confirmed intact rather than truncated. False means nothing on disk changed, and the
    // caller has to say so rather than report a confirmation BEM does not hold.
    private bool Write(IReadOnlyList<LearnedNexusId> learned)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath!)!);

            var pending = FilePath + ".tmp";

            File.WriteAllText(pending, JsonSerializer.Serialize(learned, Format));
            File.Move(pending, FilePath!, overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
