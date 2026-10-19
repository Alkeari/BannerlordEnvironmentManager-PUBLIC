using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public enum XmlOverlapGrade
{
    // One community module setting a value the base game had set. This is how the game is meant to be
    // rebalanced, so it is information rather than a fault.
    NativeOverride,

    // Two or more community modules, where one of them declares a dependency on another. The author
    // knew about the other mod and the engine enforces the order, so the winner is the intended one.
    DeclaredLayering,

    // Two or more community modules with nothing tying them together. Which one wins is an accident
    // of where they happen to sort, and the loser's value is discarded with no error anywhere.
    UndeclaredConflict
}

// What the engine did with the losing value.
public enum XmlContestKind
{
    // Two modules set the same attribute on the same element to different values. MergeElementAttributes
    // calls SetAttributeValue for each of the later module's attributes, so the later value stands.
    Attribute,

    // Two modules give the same element different non-empty text. MergeElements assigns element2's text
    // over element1's, which also discards element1's child elements.
    Text,

    // A module marks an element _replaceWhileMerging="true". The engine wipes every attribute and child
    // an earlier module contributed to that element before applying this one.
    WholesaleReplacement
}

public sealed record XmlDefiner(ModuleId ModuleId, string DisplayName, int LoadOrderIndex, bool IsOfficial);

public sealed record XmlContestedValue(XmlDefiner Definer, string Value);

// One value the game silently drops. This is the unit a person can act on: not "two mods both edit
// Items", which is true of nearly every content mod, but "these two disagree about this attribute of
// this entry, and this is the one that survives".
public sealed record XmlContest(
    string Dataset,
    string EntryId,
    string ElementPath,
    string Attribute,
    XmlContestKind Kind,
    IReadOnlyList<XmlContestedValue> Values)
{
    public XmlContestedValue Winner => Values[^1];

    private IEnumerable<XmlContestedValue> Losers => Values.Take(Values.Count - 1);

    public string Describe() => Kind switch
    {
        XmlContestKind.WholesaleReplacement =>
            Strings.Current.Format(
                "Core.Modules.XmlOverlap.Contest.Replaced",
                EntryId, Winner.Definer.DisplayName, string.Join(", ", Losers.Select(v => v.Definer.DisplayName))),
        XmlContestKind.Text =>
            Strings.Current.Format(
                "Core.Modules.XmlOverlap.Contest.Text",
                EntryId,
                string.Join(", ", Losers.Select(v => Strings.Current.Format("Core.Modules.XmlOverlap.Contest.SetsQuoted", v.Definer.DisplayName, v.Value))),
                Winner.Definer.DisplayName,
                Winner.Value),
        _ =>
            Strings.Current.Format(
                "Core.Modules.XmlOverlap.Contest.Attribute",
                EntryId,
                Attribute,
                string.Join(", ", Losers.Select(v => Strings.Current.Format("Core.Modules.XmlOverlap.Contest.Sets", v.Definer.DisplayName, v.Value))),
                Winner.Definer.DisplayName,
                Winner.Value)
    };
}

// Every contest over one attribute of one kind of element, across every entry it happens to. The
// dataset is the heading; the finding is the attribute.
public sealed record XmlOverlapGroup(
    string Dataset,
    XmlOverlapGrade Grade,
    IReadOnlyList<XmlDefiner> Definers,
    IReadOnlyList<string> EntryIds,
    string ElementPath = "",
    string Attribute = "",
    IReadOnlyList<XmlContest> Contests = null!)
{
    public IReadOnlyList<XmlContest> Contests { get; init; } = Contests ?? [];

    // MergeElementAttributes applies each module's attributes in load order, so the last module to set
    // this attribute is the one whose value the game keeps.
    public XmlDefiner Winner => Definers[^1];
}

// Why a registration produced no entries. Three different situations wore one sentence between them,
// and only the first is anything to do with BEM.
public enum XmlDatasetProblemKind
{
    // BEM found the file and could not read it: broken XML, an empty document, an IO failure. This is
    // the only one of the three where the counts above are incomplete.
    Unreadable,

    // The module registers a name and ships nothing under it, in any of the forms the engine looks for.
    // The game loads nothing for it either, and only the module's author can change that.
    NotShipped,

    // The module ships the file somewhere a different part of the engine reads it. The merge pipeline
    // is not where it was ever going to be found, so nothing is wrong.
    LoadedElsewhere
}

public sealed record XmlDatasetProblem(
    ModuleId ModuleId,
    string DisplayName,
    string Dataset,
    string Path,
    string Reason,
    XmlDatasetProblemKind Kind = XmlDatasetProblemKind.Unreadable);

// What one module's XSLT registration actually did to the dataset it transforms, measured by running
// it at the point in the pipeline the engine runs it.
public sealed record XmlTransformOutcome(
    ModuleId ModuleId,
    string DisplayName,
    string Dataset,
    string Path,
    int Added,
    int Changed,
    int Removed,
    string? Error = null,
    int Overrode = 0,
    bool Applied = true,
    // How many entries the dataset held when the stylesheet ran. Without it a removal count carries no
    // proportion, and "555 removed" reads exactly like "3 removed".
    int Before = 0,
    // How many entries the dataset still has once every module has contributed. Minus one means nobody
    // stamped it, which is not the same as zero and never reports as empty.
    int Survivors = -1)
{
    public bool Ran => Error is null && Applied;

    // The degenerate case, and the one worth interrupting for: the dataset is empty at the end of the
    // pipeline. A stylesheet runs against the merged document, so clearing it discards what the game and
    // every earlier module put there, not only what the module shipping the stylesheet contributed.
    //
    // Judged on the finished dataset rather than on the moment the stylesheet ran, because wiping a
    // dataset and refilling it is how a total conversion legitimately replaces the game's data. Culture
    // Diversity Mod does exactly that to BodyProperties on the reference install, and grading it by the
    // intermediate state made a working mod look fatal.
    public bool EmptiesDataset => Ran && Before > 0 && Survivors == 0;

    public string Describe()
    {
        // CreateMergedXmlFile loads the first entry of its list and starts its loop at index 1, so the
        // stylesheet belonging to the first module to register a dataset is never applied to anything.
        if (!Applied)
            return Strings.Current.Format("Core.Modules.XmlOverlap.Transform.NotApplied", DisplayName, Dataset);

        if (Error is not null)
            return Strings.Current.Format("Core.Modules.XmlOverlap.Transform.Error", DisplayName, Dataset, Error);

        if (Added + Changed + Removed == 0)
            return Strings.Current.Format("Core.Modules.XmlOverlap.Transform.NoChange", DisplayName, Dataset);

        if (EmptiesDataset)
            return Strings.Current.Format("Core.Modules.XmlOverlap.Transform.Emptied", DisplayName, Dataset, Before);

        var summary = Strings.Current.Format("Core.Modules.XmlOverlap.Transform.Summary", DisplayName, Dataset, Added, Changed, Removed);

        // The stylesheet runs after every module ahead of it in the load order, so where it rewrites an
        // entry one of them defined, its value is the one carried forward. Whether a later module then
        // takes it back is an ordinary conflict and is reported as one.
        return Overrode == 0
            ? summary
            : summary + Strings.Current.Format("Core.Modules.XmlOverlap.Transform.Overrode", Overrode);
    }
}

public sealed record XmlOverlapReport(
    int ModulesWithDatasets,
    int FilesParsed,
    int EntriesIndexed,
    int RawOverlapCount,
    int OfficialOnlyCount,
    IReadOnlyList<XmlOverlapGroup> Groups,
    IReadOnlyList<XmlDatasetProblem> Problems,
    IReadOnlyList<XmlTransformOutcome> Transforms = null!,
    int ContestedValues = 0,
    int ComposedOverlapCount = 0)
{
    public IReadOnlyList<XmlTransformOutcome> Transforms { get; init; } = Transforms ?? [];

    public static XmlOverlapReport Empty { get; } = new(0, 0, 0, 0, 0, [], []);
}

// Every mod that adds or rebalances game content registers its XML in SubModule.xml under a dataset id,
// and MBObjectManager merges every module's file for that id into one document.
//
// The merge is attribute by attribute, not file by file and not element by element. MergeElementAttributes
// calls SetAttributeValue for each attribute the later document names and leaves every other attribute
// alone, and MergeElements matches child elements by the unique attributes the dataset's XSD declares,
// recursing into a match and appending anything unmatched. Two mods that both edit an item compose
// cleanly; a value is lost only where they set the same attribute to different values.
//
// A module may also ship an XSLT stylesheet, and the pipeline is followed here in full rather than
// approximated. GetMergedXmlForManaged builds two parallel lists, one slot per file, and registers a
// stylesheet in all three of its branches: beside a Name.xml, beside every file of a Name directory, and
// on its own where neither exists. CreateMergedXmlFile then walks those slots in load order, applying
// each slot's stylesheet to everything accumulated so far before merging that slot's own XML on top.
// Its loop starts at index 1, so the first slot a dataset gets never has its stylesheet applied at all.
public static class XmlDatasetOverlaps
{
    // Registered documents nest at most one level before the entries start: Items holds Item, and
    // GameText holds strings which holds string. Below that an id is a reference to another object,
    // such as the item an equipment slot points at, and counting those would pair every roster in the
    // game with every item it equips.
    private const int MaxEntryDepth = 3;

    // The attribute MergeElementAttributes reads to mean "throw away what came before". Nothing on the
    // real install carries it, so this path is proved by fixture rather than by real data.
    private const string ReplaceMarker = "_replaceWhileMerging";

    // A stylesheet is code an unknown mod author wrote, and XslCompiledTransform has neither a timeout
    // nor an output limit. The output stream is what stops a runaway one, so the scan cannot hang or
    // exhaust memory on a transform that never terminates on its own.
    private const long MaxTransformBytes = 64L * 1024 * 1024;

    private static readonly TimeSpan TransformBudget = TimeSpan.FromSeconds(15);

    public static XmlOverlapReport Inspect(IReadOnlyList<ModuleEntry> loadOrder)
    {
        ArgumentNullException.ThrowIfNull(loadOrder);

        var definers = new List<XmlDefiner>();
        var declared = new Dictionary<ModuleId, IReadOnlySet<ModuleId>>();
        var problems = new List<XmlDatasetProblem>();
        var outcomes = new List<XmlTransformOutcome>();
        var definitions = new Dictionary<DatasetEntry, List<int>>();
        var merges = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var schemas = new SchemaCatalog(SchemasFolder(loadOrder));
        var contests = new ContestLog(definers);
        var contributors = new HashSet<ModuleId>();

        // How many slots each dataset has already been given. CreateMergedXmlFile loads slot 0 and starts
        // its loop at 1, so this is what decides whether a stylesheet is applied or skipped.
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        var filesParsed = 0;
        var entriesIndexed = 0;

        for (var i = 0; i < loadOrder.Count; i++)
        {
            var entry = loadOrder[i];

            if (!entry.IsEnabled || entry.Manifest is null)
                continue;

            // Every enabled module takes its place here before its files are read, so the index a
            // contest records always names the module that recorded it. Which of them contributed
            // anything is a separate question, answered below.
            var definer = new XmlDefiner(entry.Id, entry.DisplayName, i, entry.IsOfficial);
            var index = definers.Count;
            var registered = false;

            definers.Add(definer);
            declared[entry.Id] = entry.Dependencies.Select(d => d.TargetId).ToHashSet();

            foreach (var registration in ReadRegistrations(entry.Manifest.ManifestPath))
            {
                foreach (var slot in Resolve(entry.Manifest.FolderPath, registration, definer, problems))
                {
                    var ordinal = slots.TryGetValue(registration.Dataset, out var used) ? used : 0;

                    slots[registration.Dataset] = ordinal + 1;

                    if (slot.StylesheetPath is not null)
                    {
                        filesParsed++;

                        var outcome = ordinal == 0
                            ? new XmlTransformOutcome(entry.Id, definer.DisplayName, registration.Dataset,
                                slot.StylesheetPath, 0, 0, 0, null, 0, Applied: false)
                            : Apply(
                                merges.TryGetValue(registration.Dataset, out var root) ? root : null,
                                registration.Dataset,
                                slot.StylesheetPath,
                                definer,
                                index,
                                definitions,
                                merges,
                                contests);

                        outcomes.Add(outcome);

                        if (outcome.Added + outcome.Changed > 0)
                            registered = true;
                    }

                    if (slot.XmlPath is null)
                        continue;

                    filesParsed++;

                    var loaded = Load(slot.XmlPath, definer, registration.Dataset, problems);

                    if (loaded is null)
                        continue;

                    foreach (var (id, _) in ReadEntries(loaded))
                    {
                        entriesIndexed++;
                        registered = true;

                        Record(definitions, registration.Dataset, id, index);
                    }

                    contests.Dataset = registration.Dataset;
                    contests.Definer = index;

                    Merge(merges, registration.Dataset, loaded, schemas.For(registration.Dataset), contests);
                }
            }

            if (registered || problems.Any(p => p.ModuleId == entry.Id))
                contributors.Add(entry.Id);
        }

        // Stamped after every module has contributed. A stylesheet that clears a dataset a later module
        // then refills has emptied nothing, and only the finished document can say which happened.
        for (var i = 0; i < outcomes.Count; i++)
        {
            outcomes[i] = outcomes[i] with
            {
                Survivors = merges.TryGetValue(outcomes[i].Dataset, out var final)
                    ? ReadEntries(final).Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count()
                    : 0
            };
        }

        return Build(definers, declared, contributors.Count, definitions, problems, outcomes, contests,
            filesParsed, entriesIndexed);
    }

    // The XSDs the engine merges against ship with the game, beside the Modules folder that every
    // module was scanned from. Without them the merge still runs, keyed on id, which is what four out
    // of five of the shipped schemas declare anyway.
    private static string? SchemasFolder(IReadOnlyList<ModuleEntry> loadOrder)
    {
        foreach (var entry in loadOrder)
        {
            if (entry.Manifest is null)
                continue;

            try
            {
                var schemas = Path.GetFullPath(Path.Combine(entry.Manifest.FolderPath, "..", "..", "XmlSchemas"));

                if (Directory.Exists(schemas))
                    return schemas;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        return null;
    }

    // One module can spread a dataset over several files and can even repeat an id inside one of them.
    // Neither is a conflict with anybody.
    private static void Record(Dictionary<DatasetEntry, List<int>> definitions, string dataset, string id, int index)
    {
        var key = new DatasetEntry(dataset, id);

        if (!definitions.TryGetValue(key, out var owners))
            definitions[key] = owners = [];

        if (owners.Count == 0 || owners[^1] != index)
            owners.Add(index);
    }

    private static XmlOverlapReport Build(
        List<XmlDefiner> definers,
        Dictionary<ModuleId, IReadOnlySet<ModuleId>> declared,
        int modulesWithDatasets,
        Dictionary<DatasetEntry, List<int>> definitions,
        List<XmlDatasetProblem> problems,
        List<XmlTransformOutcome> outcomes,
        ContestLog contests,
        int filesParsed,
        int entriesIndexed)
    {
        var raw = 0;
        var officialOnly = 0;

        foreach (var owners in definitions.Values)
        {
            if (owners.Count < 2)
                continue;

            raw++;

            // The base game ships its data layered across Native, SandBoxCore, Sandbox and StoryMode
            // and they redefine each other on purpose. The user installed none of it and can act on
            // none of it, so pairing them would be noise with no remedy.
            if (owners.TrueForAll(o => definers[o].IsOfficial))
                officialOnly++;
        }

        var grouped = new Dictionary<GroupKey, List<XmlContest>>();
        var contested = new HashSet<DatasetEntry>();

        foreach (var contest in contests.Resolve())
        {
            var community = contest.Values.Where(v => !v.Definer.IsOfficial).Select(v => v.Definer).ToList();

            // Two official modules disagreeing is the base game layering itself, which the user
            // installed none of and can act on none of.
            if (community.Count == 0)
                continue;

            contested.Add(new DatasetEntry(contest.Dataset, contest.EntryId));

            var grade = community.Count == 1
                ? XmlOverlapGrade.NativeOverride
                : KnowsAboutEachOther(declared, community)
                    ? XmlOverlapGrade.DeclaredLayering
                    : XmlOverlapGrade.UndeclaredConflict;

            // The finding is the contested attribute, not the dataset and not the pair of mods. Two
            // mods both editing Items is true of nearly every content mod and tells the user nothing;
            // "these disagree about Item weight, on these 153 items" is the thing they can act on. Which
            // mods argued over each individual entry, and which value won it, stays on the contest.
            var key = new GroupKey(contest.Dataset, contest.ElementPath, contest.Attribute, grade);

            if (!grouped.TryGetValue(key, out var found))
                grouped[key] = found = [];

            found.Add(contest);
        }

        var groups = grouped
            .Select(g => new XmlOverlapGroup(
                g.Key.Dataset,
                g.Key.Grade,
                [.. g.Value
                    .SelectMany(c => c.Values.Select(v => v.Definer))
                    .Where(d => g.Key.Grade != XmlOverlapGrade.NativeOverride || !d.IsOfficial)
                    .DistinctBy(d => d.LoadOrderIndex)
                    .OrderBy(d => d.LoadOrderIndex)],
                [.. g.Value.Select(c => c.EntryId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                g.Key.ElementPath,
                g.Key.Attribute,
                [.. g.Value]))
            .OrderBy(g => g.Grade switch
            {
                XmlOverlapGrade.UndeclaredConflict => 0,
                XmlOverlapGrade.DeclaredLayering => 1,
                _ => 2
            })
            .ThenByDescending(g => g.EntryIds.Count)
            .ThenBy(g => g.Dataset, StringComparer.Ordinal)
            .ThenBy(g => g.Attribute, StringComparer.Ordinal)
            .ToList();

        return new XmlOverlapReport(
            modulesWithDatasets,
            filesParsed,
            entriesIndexed,
            raw,
            officialOnly,
            groups,
            [.. problems],
            [.. outcomes],
            groups.Sum(g => g.Contests.Count),
            raw - officialOnly - contested.Count);
    }

    private static bool KnowsAboutEachOther(
        Dictionary<ModuleId, IReadOnlySet<ModuleId>> declared,
        List<XmlDefiner> community)
    {
        var ids = community.Select(c => c.ModuleId).ToHashSet();

        return community.Any(c =>
            declared.TryGetValue(c.ModuleId, out var targets)
            && targets.Any(target => target != c.ModuleId && ids.Contains(target)));
    }

    private sealed record DatasetEntry(string Dataset, string Id);

    private sealed record GroupKey(string Dataset, string ElementPath, string Attribute, XmlOverlapGrade Grade);

    private sealed record Registration(string Dataset, string Path);

    // One entry of the two parallel lists GetMergedXmlForManaged builds. Either half can be absent: a
    // module can ship XML with no stylesheet, a stylesheet with no XML, or both together.
    private sealed record ResolvedSlot(string? XmlPath, string? StylesheetPath);

    // Which module last set a given attribute. Kept on the attribute itself so the merged document
    // carries its own provenance, and shared per module so 238 objects cover every attribute in the
    // install rather than one object each.
    private sealed class Owner(int index)
    {
        public int Index { get; } = index;
    }

    // Contests accumulate as the merge walks, one event per overwrite, and are folded at the end into
    // one row per attribute per entry carrying the whole chain of values.
    private sealed class ContestLog(List<XmlDefiner> definers)
    {
        private readonly Dictionary<ContestKey, List<(int Definer, string Value)>> events = [];
        private readonly List<Owner> owners = [];

        public string Dataset { get; set; } = string.Empty;

        public int Definer { get; set; }

        public Owner Claim(int index)
        {
            while (owners.Count <= index)
                owners.Add(new Owner(owners.Count));

            return owners[index];
        }

        public void Add(string? entryId, string elementPath, string attribute, XmlContestKind kind, int previous,
            string previousValue, string value)
        {
            if (previous < 0 || previous == Definer)
                return;

            var key = new ContestKey(Dataset, entryId ?? string.Empty, elementPath, attribute, kind);

            if (!events.TryGetValue(key, out var chain))
                events[key] = chain = [(previous, previousValue)];
            else if (chain[^1].Definer != previous)
                chain.Add((previous, previousValue));

            chain.Add((Definer, value));
        }

        public IEnumerable<XmlContest> Resolve()
        {
            foreach (var (key, chain) in events)
            {
                // A definer that set a value and was overwritten, then set it again later, is still one
                // party to the argument. Its last word is the one the chain keeps.
                var values = new List<XmlContestedValue>();

                foreach (var (definer, value) in chain)
                {
                    if (definer >= definers.Count)
                        continue;

                    values.RemoveAll(v => v.Definer.LoadOrderIndex == definers[definer].LoadOrderIndex);
                    values.Add(new XmlContestedValue(definers[definer], value));
                }

                // A module whose value is the one the game ends up with lost nothing, even if something
                // in between briefly overwrote it. Three mods that all settle on 0.4 with one 0.5 in
                // the middle is an argument between two of them, not among four.
                var winner = values[^1];

                values.RemoveAll(v => v != winner && v.Value == winner.Value);

                if (values.Count > 1)
                    yield return new XmlContest(key.Dataset, key.EntryId, key.ElementPath, key.Attribute, key.Kind, values);
            }
        }

        private sealed record ContestKey(
            string Dataset,
            string EntryId,
            string ElementPath,
            string Attribute,
            XmlContestKind Kind);
    }

    // Module.LoadSubModules reads the game data a module contributes from this node and nowhere else.
    // An XML file sitting in ModuleData that nothing here names is never loaded, so reporting overlaps
    // in it would accuse two mods of fighting over data the engine never reads.
    private static IReadOnlyList<Registration> ReadRegistrations(string manifestPath)
    {
        XDocument document;

        try
        {
            document = XDocument.Load(manifestPath);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return [];
        }

        if (document.Root is null)
            return [];

        var registrations = new List<Registration>();

        foreach (var node in document.Root.Descendants("XmlNode"))
        {
            var name = node.Element("XmlName");
            var dataset = name?.Attribute("id")?.Value;
            var path = name?.Attribute("path")?.Value;

            if (string.IsNullOrWhiteSpace(dataset) || string.IsNullOrWhiteSpace(path))
                continue;

            // A multiplayer-only registration is loaded into a multiplayer session, where the campaign
            // data it would collide with is never loaded at all. Native's mpitems and SandBoxCore's
            // items both register as Items and share hundreds of ids without ever meeting.
            var types = node.Element("IncludedGameTypes")?.Elements("GameType")
                .Select(t => t.Attribute("value")?.Value)
                .ToList();

            if (types is { Count: > 0 } && types.All(t => t == "MultiplayerGame"))
                continue;

            registrations.Add(new Registration(dataset, path));
        }

        return registrations;
    }

    // ModuleHelper.GetXsltPath returns the registered name with ".xsl" on the end, and HandleXsltList
    // tries that path first and then the same path with a "t" appended. So a stylesheet is ".xsl" if
    // there is one and ".xslt" otherwise, and both spellings are the engine's own.
    private static string? Stylesheet(string target)
    {
        if (File.Exists(target + ".xsl"))
            return target + ".xsl";

        return File.Exists(target + ".xslt") ? target + ".xslt" : null;
    }

    private static IEnumerable<ResolvedSlot> Resolve(
        string moduleFolder,
        Registration registration,
        XmlDefiner definer,
        List<XmlDatasetProblem> problems)
    {
        string target;

        try
        {
            target = Path.Combine(
                moduleFolder,
                "ModuleData",
                registration.Path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        }
        catch (ArgumentException)
        {
            problems.Add(Dangling(moduleFolder, registration, definer));
            yield break;
        }

        // Branch one: the module ships Name.xml. The engine queues the file and registers the stylesheet
        // beside it, and both apply, the stylesheet first.
        if (File.Exists(target + ".xml"))
        {
            yield return new ResolvedSlot(target + ".xml", Stylesheet(target));
            yield break;
        }

        // Branch two: the module ships a Name directory instead, which is how a mod splits one dataset
        // across a file per culture. Every file in it is its own slot with its own sibling stylesheet.
        if (Directory.Exists(target))
        {
            string[] files;

            try
            {
                files = Directory.GetFiles(target, "*.xml", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(new XmlDatasetProblem(definer.ModuleId, definer.DisplayName, registration.Dataset,
                    target, Strings.Current.Format("Core.Modules.XmlOverlap.Problem.FolderUnreadable", ex.Message),
                    XmlDatasetProblemKind.Unreadable));
                yield break;
            }

            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                yield return new ResolvedSlot(file, Stylesheet(file[..^4]));

            yield break;
        }

        // Branch three: the module ships neither, and the engine queues an empty slot with the stylesheet
        // still registered against it. The transform applies and there is nothing to merge afterwards.
        if (Stylesheet(target) is { } stylesheet)
        {
            yield return new ResolvedSlot(null, stylesheet);
            yield break;
        }

        problems.Add(Dangling(moduleFolder, registration, definer));
    }

    // Nothing under ModuleData answers to this name. That is a statement about the module, not about
    // BEM, so before making it the two other places a module can put a file it registers are checked.
    private static XmlDatasetProblem Dangling(string moduleFolder, Registration registration, XmlDefiner definer)
    {
        var name = registration.Path.Replace('\\', '/').TrimEnd('/');
        var leaf = name[(name.LastIndexOf('/') + 1)..];

        if (Prefabs(moduleFolder, name, leaf) is { } prefabs)
        {
            return new XmlDatasetProblem(definer.ModuleId, definer.DisplayName, registration.Dataset, prefabs,
                Strings.Current["Core.Modules.XmlOverlap.Problem.LoadedAsPrefab"],
                XmlDatasetProblemKind.LoadedElsewhere);
        }

        if (Localized(moduleFolder, leaf) is { } languages)
        {
            return new XmlDatasetProblem(definer.ModuleId, definer.DisplayName, registration.Dataset, languages,
                Strings.Current["Core.Modules.XmlOverlap.Problem.LoadedAsLocalization"],
                XmlDatasetProblemKind.LoadedElsewhere);
        }

        return new XmlDatasetProblem(definer.ModuleId, definer.DisplayName, registration.Dataset,
            Describe(moduleFolder, registration),
            Strings.Current["Core.Modules.XmlOverlap.Problem.NotShipped"],
            XmlDatasetProblemKind.NotShipped);
    }

    // UIResourceManager adds every module's GUI folder to the resource depot and the widget factory
    // collects prefabs out of it, so a registration naming a folder of prefabs is served there and never
    // goes near MBObjectManager. Registering them under Xmls as well is a widespread modding habit.
    private static string? Prefabs(string moduleFolder, string name, string leaf)
    {
        try
        {
            var gui = Path.Combine(moduleFolder, "GUI");
            var candidates = new[]
            {
                Path.Combine(gui, "Prefabs", name.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(gui, name.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(gui, "Prefabs", leaf)
            };

            foreach (var candidate in candidates)
            {
                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.xml").Any())
                    return candidate;

                if (File.Exists(candidate + ".xml"))
                    return candidate + ".xml";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    // LocalizedTextManager.LoadLocalizationXmls reads only files named language_data.xml under
    // ModuleData/Languages, and each of those lists its string files by xml_path relative to that
    // folder. A registered name that turns up in one of those manifests is loaded by the localization
    // system, so saying the game loads nothing for it would be a false claim about the game.
    private static string? Localized(string moduleFolder, string leaf)
    {
        string root;

        try
        {
            root = Path.Combine(moduleFolder, "ModuleData", "Languages");

            if (!Directory.Exists(root))
                return null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        IEnumerable<string> manifests;

        try
        {
            manifests = Directory.EnumerateFiles(root, "language_data.xml", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var manifest in manifests)
        {
            XDocument document;

            try
            {
                document = XDocument.Load(manifest);
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var declared in document.Descendants("LanguageFile"))
            {
                var value = declared.Attribute("xml_path")?.Value;

                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var candidate = value.Replace('\\', '/').TrimEnd('/');

                candidate = candidate[(candidate.LastIndexOf('/') + 1)..];

                if (candidate.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    candidate = candidate[..^4];

                if (string.Equals(candidate, leaf, StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(root, value.Replace('/', Path.DirectorySeparatorChar));
            }
        }

        return null;
    }

    private static string Describe(string moduleFolder, Registration registration)
    {
        try
        {
            return Path.Combine(
                moduleFolder,
                "ModuleData",
                registration.Path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar))
                + ".xml";
        }
        catch (ArgumentException)
        {
            return registration.Path;
        }
    }

    private static XElement? Load(
        string path,
        XmlDefiner definer,
        string dataset,
        List<XmlDatasetProblem> problems)
    {
        try
        {
            var root = XDocument.Load(path).Root;

            if (root is null)
                problems.Add(new XmlDatasetProblem(definer.ModuleId, definer.DisplayName, dataset, path,
                    Strings.Current["Core.Modules.XmlOverlap.Problem.Empty"], XmlDatasetProblemKind.Unreadable));

            return root;
        }
        catch (XmlException ex)
        {
            problems.Add(new XmlDatasetProblem(definer.ModuleId, definer.DisplayName, dataset, path,
                Strings.Current.Format("Core.Modules.XmlOverlap.Problem.InvalidXml", ex.Message),
                XmlDatasetProblemKind.Unreadable));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new XmlDatasetProblem(definer.ModuleId, definer.DisplayName, dataset, path,
                Strings.Current.Format("Core.Modules.XmlOverlap.Problem.Unreadable", ex.Message), XmlDatasetProblemKind.Unreadable));
            return null;
        }
    }

    private static void Merge(
        Dictionary<string, XElement> merges,
        string dataset,
        XElement root,
        Schema schema,
        ContestLog contests)
    {
        if (!merges.TryGetValue(dataset, out var merged))
        {
            merges[dataset] = merged = new XElement(root);
            Claim(merged, contests, contests.Definer);
            return;
        }

        MergeElements(merged, root, $"/{merged.Name}", null, schema, contests);
    }

    // MBObjectManager.MergeElements, followed step for step, with every overwrite recorded on the way
    // past. Reimplementing it rather than approximating it is the whole point: the question "is this a
    // conflict" has exactly one correct answer and the engine is holding it.
    private static void MergeElements(
        XElement into,
        XElement from,
        string path,
        string? entryId,
        Schema schema,
        ContestLog contests)
    {
        var replace = from.Attributes().Any(a => a.Name.LocalName == ReplaceMarker && a.Value == "true");

        if (replace)
        {
            foreach (var destroyed in into.Attributes())
            {
                contests.Add(entryId, path, destroyed.Name.ToString(), XmlContestKind.WholesaleReplacement,
                    OwnerOf(destroyed), destroyed.Value, from.Attribute(destroyed.Name)?.Value ?? "(removed)");
            }

            into.RemoveAttributes();
        }

        foreach (var attribute in from.Attributes())
        {
            var existing = into.Attribute(attribute.Name);

            if (existing is not null && existing.Value != attribute.Value)
            {
                contests.Add(entryId, path, attribute.Name.ToString(), XmlContestKind.Attribute,
                    OwnerOf(existing), existing.Value, attribute.Value);
            }

            into.SetAttributeValue(attribute.Name, attribute.Value);

            var claimed = into.Attribute(attribute.Name);

            if (claimed is not null)
            {
                claimed.RemoveAnnotations<Owner>();
                claimed.AddAnnotation(contests.Claim(contests.Definer));
            }
        }

        // Assigning Value replaces every child element with a text node, so text that both documents
        // carry is a genuine loss and not only of the text.
        if (into.Value.Length > 0 && from.Value.Length > 0)
        {
            if (into.Value != from.Value)
            {
                contests.Add(entryId, path, string.Empty, XmlContestKind.Text,
                    OwnerOf(into), into.Value, from.Value);
            }

            into.Value = from.Value;
        }

        if (replace)
            into.Elements().Remove();

        var byName = new Dictionary<XName, Siblings>();

        foreach (var child in into.Elements())
        {
            if (!byName.TryGetValue(child.Name, out var siblings))
                byName[child.Name] = siblings = new Siblings(child);

            siblings.Keyed[Key(child, schema.UniqueAttributes($"{path}/{child.Name}"))] = child;
            siblings.Count++;
        }

        foreach (var child in from.Elements())
        {
            var childPath = $"{path}/{child.Name}";
            var childEntry = entryId ?? child.Attribute("id")?.Value;

            if (byName.TryGetValue(child.Name, out var siblings))
            {
                if (schema.AlwaysPreferMerge(childPath))
                {
                    MergeElements(siblings.First, child, childPath, childEntry, schema, contests);
                    continue;
                }

                var key = Key(child, schema.UniqueAttributes(childPath));

                if (key.Length > 0 && siblings.Keyed.TryGetValue(key, out var match))
                {
                    MergeElements(match, child, childPath, childEntry, schema, contests);
                    continue;
                }

                if (child.Attributes().Any(a => a.Name.LocalName == ReplaceMarker && a.Value == "true"))
                {
                    MergeElements(siblings.First, child, childPath, childEntry, schema, contests);
                    continue;
                }

                // No shipped schema describes this element, so there is no declared key to match on and
                // no declared merge hint. A container that occurs exactly once on both sides is the
                // shape every shipped schema marks AlwaysPreferMerge, so it is treated that way rather
                // than appended, which would hide every disagreement underneath it.
                if (!schema.Describes(childPath) && siblings.Count == 1 && key.Length == 0)
                {
                    MergeElements(siblings.First, child, childPath, childEntry, schema, contests);
                    continue;
                }
            }

            // Unmatched, so the engine appends it and nothing is lost. The lookup deliberately is not
            // updated: MergeElements builds it once before its loop, so a document that repeats a key
            // within itself appends twice there too.
            var clone = new XElement(child);

            Claim(clone, contests, contests.Definer);
            into.Add(clone);
        }
    }

    private sealed class Siblings(XElement first)
    {
        public XElement First { get; } = first;

        public Dictionary<string, XElement> Keyed { get; } = new(StringComparer.Ordinal);

        public int Count { get; set; }
    }

    private static string Key(XElement element, IReadOnlyList<string> unique)
    {
        if (unique.Count == 1)
            return element.Attribute(unique[0])?.Value ?? string.Empty;

        var key = new StringBuilder();

        foreach (var attribute in unique)
            key.Append(element.Attribute(attribute)?.Value ?? string.Empty);

        return key.ToString();
    }

    private static void Claim(XElement element, ContestLog contests, int index)
    {
        var owner = contests.Claim(index);

        element.RemoveAnnotations<Owner>();
        element.AddAnnotation(owner);

        foreach (var attribute in element.Attributes())
        {
            attribute.RemoveAnnotations<Owner>();
            attribute.AddAnnotation(owner);
        }

        foreach (var child in element.Elements())
            Claim(child, contests, index);
    }

    private static int OwnerOf(XObject node) => node.Annotation<Owner>()?.Index ?? -1;

    // A stylesheet's output is a fresh tree with no provenance on it, so the entry it rewrote has to be
    // given one back. It is the author only of what it actually changed: an attribute it copied through
    // still belongs to whichever module last wrote that value, and attributing the whole entry to the
    // stylesheet would erase exactly the earlier module a later overwrite ought to name as the loser.
    private static void Reclaim(XElement fresh, XElement? original, ContestLog contests, int index)
    {
        var owner = contests.Claim(index);

        fresh.RemoveAnnotations<Owner>();
        fresh.AddAnnotation(original?.Annotation<Owner>() ?? owner);

        foreach (var attribute in fresh.Attributes())
        {
            var previous = original?.Attribute(attribute.Name);

            attribute.RemoveAnnotations<Owner>();
            attribute.AddAnnotation(previous is not null && previous.Value == attribute.Value
                ? previous.Annotation<Owner>() ?? owner
                : owner);
        }

        // Children carry no id at this depth often enough that name and position is the only pairing
        // available, and it is the one that holds for a stylesheet built on the identity template.
        var seen = new Dictionary<XName, int>();

        foreach (var child in fresh.Elements())
        {
            var ordinal = seen.TryGetValue(child.Name, out var count) ? count : 0;

            seen[child.Name] = ordinal + 1;

            Reclaim(child, original?.Elements(child.Name).ElementAtOrDefault(ordinal), contests, index);
        }
    }

    private static XmlTransformOutcome Apply(
        XElement? merged,
        string dataset,
        string path,
        XmlDefiner definer,
        int index,
        Dictionary<DatasetEntry, List<int>> definitions,
        Dictionary<string, XElement> merges,
        ContestLog contests)
    {
        if (merged is null)
        {
            return new XmlTransformOutcome(definer.ModuleId, definer.DisplayName, dataset, path, 0, 0, 0,
                "nothing loaded this dataset before it, so the transform had no document to change");
        }

        var before = new Dictionary<string, long>(StringComparer.Ordinal);
        var originals = new Dictionary<string, XElement>(StringComparer.Ordinal);

        foreach (var (id, element) in ReadEntries(merged))
        {
            before[id] = Fingerprint(element);
            originals[id] = element;
        }

        XElement transformed;

        try
        {
            transformed = RunStylesheet(path, merged);
        }
        // A stylesheet is somebody else's code and can fail in ways no list of exception types covers.
        // One bad transform must cost its own row and nothing else, so this catches everything and
        // hands the reason back rather than ending the scan.
        catch (Exception ex)
        {
            return new XmlTransformOutcome(definer.ModuleId, definer.DisplayName, dataset, path, 0, 0, 0, ex.Message);
        }

        var added = 0;
        var changed = 0;
        var overrode = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (id, element) in ReadEntries(transformed))
        {
            if (!seen.Add(id))
                continue;

            if (!before.TryGetValue(id, out var was))
            {
                added++;
            }
            else if (was != Fingerprint(element))
            {
                changed++;
            }
            else
            {
                // The stylesheet copied this one through untouched, so the fresh copy inherits the
                // provenance the earlier modules built. Without this every transform would erase the
                // record of who set what for the whole dataset it runs on.
                Reclaim(element, originals[id], contests, index);
                continue;
            }

            // The stylesheet wrote this entry, so the module that ships the stylesheet owns what it
            // changed. Without this the entry would carry no provenance at all and a later module
            // overwriting it would be recorded against nobody, which is how a real conflict goes missing.
            Reclaim(element, originals.GetValueOrDefault(id), contests, index);

            // The transform runs after every module ahead of it, so where it rewrites an entry one of
            // them defined, its value is simply the later one. That is a fact, not an uncertainty.
            if (definitions.TryGetValue(new DatasetEntry(dataset, id), out var owners) && owners.Count > 0)
                overrode++;

            Record(definitions, dataset, id, index);
        }

        var removed = before.Keys.Count(id => !seen.Contains(id));

        merges[dataset] = transformed;

        return new XmlTransformOutcome(
            definer.ModuleId, definer.DisplayName, dataset, path, added, changed, removed, null, overrode,
            Before: before.Count);
    }

    // Read-only and in memory: the stylesheet is compiled from disk, run against the merged document
    // BEM built itself, and the result is parsed out of a buffer. Nothing under the game install is
    // opened for writing, and no module file is modified.
    //
    // Compiling and running both happen on a thread the scan is willing to walk away from, because
    // XslCompiledTransform offers no cancellation and a stylesheet is code somebody else wrote. Joining
    // with a budget is what guarantees the scan itself finishes; the output cap inside BoundedStream is
    // what stops a transform that terminates but floods. A walked-away-from thread is a background
    // thread and does not hold the process open, but it does keep running, so it is the price of never
    // hanging rather than a clean kill.
    internal static XElement RunStylesheet(string stylesheetPath, XElement source)
    {
        // CreateReader is bound to the caller's tree, so it is built here and handed over rather than
        // letting two threads touch the document.
        var reader = source.CreateReader();
        var name = source.Name;
        XElement? result = null;
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            try
            {
                result = Transform(stylesheetPath, reader, name);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "BEM XSLT"
        };

        worker.Start();

        if (!worker.Join(TransformBudget))
            throw new TimeoutException($"it ran for longer than the {TransformBudget.TotalSeconds:0} seconds BEM allows");

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        return result ?? new XElement(name);
    }

    private static XElement Transform(string stylesheetPath, XmlReader source, XName name)
    {
        var transform = new XslCompiledTransform();

        // Both switches off and no resolver: the stylesheet cannot embed script, cannot pull a second
        // document in through document(), and cannot resolve an import or include from disk or a URL.
        transform.Load(
            stylesheetPath,
            new XsltSettings(enableDocumentFunction: false, enableScript: false),
            stylesheetResolver: null!);

        using var buffer = new BoundedStream(MaxTransformBytes, TransformBudget);

        var settings = transform.OutputSettings?.Clone() ?? new XmlWriterSettings();
        settings.CloseOutput = false;

        using (var writer = XmlWriter.Create(buffer, settings))
            transform.Transform(source, writer);

        buffer.Position = 0;

        return XDocument.Load(buffer).Root ?? new XElement(name);
    }

    // Two elements that differ only in how they were written are the same data to the engine. An empty
    // node read from disk as <Traits></Traits> comes back from a transform as <Traits/>, so comparing
    // the serialized text made an identity stylesheet look like it replaced every entry it copied. The
    // comparison is the element name, its attributes and its text, with attribute order, layout and
    // comments normalized away, which is what the deserializer actually reads.
    private static long Fingerprint(XElement element)
    {
        var canonical = new StringBuilder();

        Canonicalize(element, canonical);

        return Hash(canonical);
    }

    private static void Canonicalize(XElement element, StringBuilder builder)
    {
        var name = element.Name.ToString();

        builder.Append('<').Append(name);

        foreach (var attribute in element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal))
            builder.Append(' ').Append(attribute.Name.ToString()).Append('=').Append(attribute.Value);

        builder.Append('>');

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child:
                    Canonicalize(child, builder);
                    break;
                case XText text when !text.Value.AsSpan().Trim().IsEmpty:
                    builder.Append(text.Value.AsSpan().Trim());
                    break;
            }
        }

        builder.Append("</").Append(name).Append('>');
    }

    // FNV-1a over the canonical form. A 32-bit hash collides once in roughly every five installs the
    // size of this one, and a collision here would read two different entries as agreeing, so the
    // width is the difference between a sound comparison and an occasional silent miss.
    private static long Hash(StringBuilder text)
    {
        var hash = 14695981039346656037UL;

        foreach (var chunk in text.GetChunks())
        {
            foreach (var character in chunk.Span)
            {
                hash = (hash ^ (byte)character) * 1099511628211UL;
                hash = (hash ^ (byte)(character >> 8)) * 1099511628211UL;
            }
        }

        return unchecked((long)hash);
    }

    private static IReadOnlyList<(string Id, XElement Element)> ReadEntries(XElement root)
    {
        var level = new List<XElement> { root };

        for (var depth = 0; depth < MaxEntryDepth; depth++)
        {
            var next = level.SelectMany(element => element.Elements()).ToList();

            if (next.Count == 0)
                return [];

            var entries = next
                .Where(element => !string.IsNullOrWhiteSpace(element.Attribute("id")?.Value))
                .Select(element => (element.Attribute("id")!.Value, element))
                .ToList();

            if (entries.Count > 0)
                return entries;

            level = next;
        }

        return [];
    }

    // The unique attributes and merge hints XmlResource.ReadXsdFileAndExtractInformation pulls out of a
    // shipped schema, read the same way and keyed on the same element paths.
    private sealed class Schema(Dictionary<string, (bool Merge, IReadOnlyList<string> Unique)> elements)
    {
        private static readonly IReadOnlyList<string> ById = ["id"];

        public static Schema Default { get; } = new([]);

        public bool AlwaysPreferMerge(string path) => elements.TryGetValue(path, out var element) && element.Merge;

        public bool Describes(string path) => elements.ContainsKey(path);

        // Without a schema every element keys on id. Four out of five of the keys the shipped schemas
        // declare are exactly that, so an unschooled dataset still merges the way the engine would
        // rather than falling back to treating every file as a wholesale replacement.
        public IReadOnlyList<string> UniqueAttributes(string path) =>
            elements.TryGetValue(path, out var element) && element.Unique.Count > 0 ? element.Unique : ById;
    }

    private sealed class SchemaCatalog(string? folder)
    {
        private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";

        private readonly Dictionary<string, Schema> cache = new(StringComparer.OrdinalIgnoreCase);

        public Schema For(string dataset)
        {
            if (cache.TryGetValue(dataset, out var schema))
                return schema;

            return cache[dataset] = Read(dataset);
        }

        private Schema Read(string dataset)
        {
            if (folder is null)
                return Schema.Default;

            string path;

            try
            {
                path = Path.Combine(folder, dataset + ".xsd");
            }
            catch (ArgumentException)
            {
                return Schema.Default;
            }

            if (!File.Exists(path))
                return Schema.Default;

            XDocument document;

            try
            {
                document = XDocument.Load(path);
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                return Schema.Default;
            }

            var elements = new Dictionary<string, (bool Merge, IReadOnlyList<string> Unique)>(StringComparer.Ordinal);
            var unique = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var element in document.Descendants(Xs + "element"))
                elements[XsdPath(element)] = (PrefersMerge(element), []);

            foreach (var key in document.Descendants(Xs + "unique").Concat(document.Descendants(Xs + "key")))
            {
                var selector = key.Element(Xs + "selector")?.Attribute("xpath")?.Value;

                if (string.IsNullOrWhiteSpace(selector))
                    continue;

                var fields = key.Elements(Xs + "field")
                    .Select(f => f.Attribute("xpath")?.Value)
                    .Where(x => x is { Length: > 1 } && x[0] == '@')
                    .Select(x => x![1..])
                    .ToList();

                if (fields.Count == 0)
                    continue;

                var keyed = XsdPath(key) + "/" + selector;

                if (!unique.TryGetValue(keyed, out var existing))
                    unique[keyed] = fields;
                else
                    existing.AddRange(fields);
            }

            foreach (var (keyed, fields) in unique)
                elements[keyed] = (elements.TryGetValue(keyed, out var element) && element.Merge, fields);

            return new Schema(elements);
        }

        private static bool PrefersMerge(XElement element) =>
            element.Element(Xs + "annotation")?.Element(Xs + "appinfo")?.Element("appSpecificNote")
                ?.Value.Trim() == "AlwaysPreferMerge";

        // XmlResource.GetFullXPathOfElement in its isXsd form: skip anything that is not an xs:element,
        // and build the path from the name or ref of the ones that are.
        private static string XsdPath(XElement element)
        {
            if (element.Name != Xs + "element")
                return element.Parent is null ? string.Empty : XsdPath(element.Parent);

            var name = element.Attribute("name")?.Value ?? element.Attribute("ref")?.Value ?? string.Empty;

            return element.Parent is null ? name : XsdPath(element.Parent) + "/" + name;
        }
    }

    // XslCompiledTransform has neither a timeout nor an output cap, so the stream it writes into is one
    // of the two places a runaway stylesheet is stopped. This one catches a transform that floods with
    // output; the thread Run joins with a budget catches one that never returns at all.
    private sealed class BoundedStream(long maxBytes, TimeSpan budget) : MemoryStream
    {
        private readonly long deadline = Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * Stopwatch.Frequency);

        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Check(1);
            base.WriteByte(value);
        }

        private void Check(int count)
        {
            if (Length + count > maxBytes)
                throw new InvalidOperationException($"it produced more than {maxBytes / 1024 / 1024} MB of output");

            if (Stopwatch.GetTimestamp() > deadline)
                throw new TimeoutException($"it ran for longer than the {budget.TotalSeconds:0} seconds BEM allows");
        }
    }
}
