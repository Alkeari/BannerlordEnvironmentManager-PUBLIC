using System.Xml;
using System.Xml.Linq;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

// How the merge dies. Each one is a specific unhandled exception out of MBObjectManager, not a guess
// about one, and each has its own remedy.
public enum XmlMergeFaultKind
{
    // MergeElements builds a lookup of the merged document's child elements and indexes the dataset's
    // schema with each one's full XPath. An element sitting at a path the schema does not declare is a
    // KeyNotFoundException with nothing in the message to say which element it was.
    UndeclaredElementPath,

    // MergeElements indexes XsdElementDictionary with the schema path first. Only the game's own
    // XmlSchemas folder is ever read into that dictionary, so a module that ships its own schema under
    // ModuleData/XmlSchemas, and a dataset with no shipped schema at all, both fail on that line.
    SchemaNeverRead,

    // GetMergedXmlForManaged queues an empty slot for a registration whose file it cannot find, and
    // CreateMergedXmlFile opens slot zero unconditionally. new StreamReader("") throws ArgumentException.
    FirstFileMissing
}

public sealed record XmlMergeContributor(ModuleId ModuleId, string DisplayName, string Path);

// One way this install's merge stops. The four facts a person needs to act are the module, the file,
// the element path and the schema that does not declare it, and all four are here as plain strings so
// the row can be copied into a bug report for the mod's author.
public sealed record XmlMergeFault(
    XmlMergeFaultKind Kind,
    string Dataset,
    string SchemaPath,
    string ElementPath,
    string EntryPath,
    IReadOnlyList<XmlMergeContributor> Sources,
    XmlMergeContributor Trigger,
    int Slot)
{
    public string Describe() => Kind switch
    {
        XmlMergeFaultKind.UndeclaredElementPath => Sources.Count == 0
            ? Strings.Current.Format(
                "Core.Modules.XmlMergeFaults.Undeclared.NoSource",
                Dataset, ElementPath, Path.GetFileName(SchemaPath), Trigger.DisplayName)
            : Strings.Current.Format(
                "Core.Modules.XmlMergeFaults.Undeclared.WithSource",
                Dataset, Names(Sources), ElementPath, Path.GetFileName(SchemaPath), Trigger.DisplayName, Where()),

        XmlMergeFaultKind.SchemaNeverRead =>
            Strings.Current.Format("Core.Modules.XmlMergeFaults.SchemaNeverRead", Dataset, Trigger.DisplayName, SchemaPath),

        _ => Strings.Current.Format("Core.Modules.XmlMergeFaults.FirstFileMissing", Dataset, Trigger.DisplayName, Trigger.Path)
    };

    // Where the trigger's edit landed, in the words of the data rather than of XML. The entry id is what
    // the user can search their mods for; the element path is what the exception was actually about.
    private string Where() =>
        EntryPath.Length > 0 ? $"the same entry ({EntryPath})" : "the same element";

    private static string Names(IReadOnlyList<XmlMergeContributor> sources) =>
        string.Join(" and ", sources.Select(s => s.DisplayName));
}

public sealed record XmlMergeFaultReport(
    int DatasetsChecked,
    int FilesMerged,
    IReadOnlyList<XmlMergeFault> Faults)
{
    public static XmlMergeFaultReport Empty { get; } = new(0, 0, []);
}

// The merge that MBObjectManager runs when a campaign starts, run here to find out whether it survives.
//
// XmlDatasetOverlaps next door answers "which value does the game keep". This answers the prior
// question: does the merge finish at all. The engine's MergeElements indexes two dictionaries with no
// TryGetValue anywhere, so a module can ship XML that is well formed, passes every validator, loads
// fine on its own, and still turns the whole merge into an unhandled KeyNotFoundException the moment a
// second module edits the same entry. MBObjectManager.LoadXML wraps only the deserialization in a
// try/catch; the merge that feeds it is outside, so the exception reaches Campaign.OnInitialize and the
// game dies on "Start a new campaign" while booting perfectly.
//
// The engine's own words, from TaleWorlds.ObjectSystem:
//
//     Dictionary<string, XsdElement> elementSchema = XmlResource.XsdElementDictionary[xsdPath];
//     ...
//     .ToDictionary(el => el.Key, el =>
//     {
//         XElement element3 = el.First();
//         List<string> uniqueAttributes =
//             elementSchema[XmlResource.GetFullXPathOfElement(element3, isXsd: false)].UniqueAttributes;
//
// The key is the element's full XPath from the document root, not its name, so an element the schema
// declares in one place and a module puts in another is missing as far as this lookup is concerned.
public static class XmlMergeFaults
{
    // The two game type ids a singleplayer campaign runs under. GetMergedXmlForManaged drops any
    // registration that names game types and does not name the running one, so which files merge depends
    // on which of these the user starts. Both are walked and identical faults are reported once.
    private static readonly string[] CampaignGameTypes = ["Campaign", "CampaignStoryMode"];

    // Every dataset id the shipped game passes to MBObjectManager.LoadXML or GetMergedXmlForManaged,
    // read out of the call sites in the game's own assemblies. An id that is registered in a SubModule.xml
    // and is not here never goes through this merge at all: it is read by the GUI prefab loader, by the
    // localization system, or by the native scene and prop loaders, none of which use MBObjectManager.
    // That distinction is what keeps a registration with no file behind it from being reported as fatal
    // when the game plainly starts with fifteen of them present.
    private static readonly HashSet<string> Merged = new(StringComparer.Ordinal)
    {
        "BannerIcons",
        "BasicCultures",
        "BodyProperties",
        "Concepts",
        "CoreParameters",
        "CraftingPieces",
        "CraftingTemplates",
        "CustomBattleScenes",
        "EquipmentRosters",
        "Factions",
        "GameText",
        "Heroes",
        "ItemModifierGroups",
        "ItemModifiers",
        "Items",
        "Kingdoms",
        "LocationComplexTemplates",
        "MPCharacters",
        "MPClassDivisions",
        "MissionShips",
        "Monsters",
        "MusicInstruments",
        "MusicTracks",
        "NPCCharacters",
        "SPCultures",
        "Settlements",
        "ShipHulls",
        "ShipPhysicsReferences",
        "ShipSlots",
        "ShipUpgradePieces",
        "SiegeEngines",
        "SkeletonScales",
        "SkillSets",
        "WeaponDescriptions",
        "WorkshopTypes",
        "partyTemplates"
    };

    public static XmlMergeFaultReport Inspect(IReadOnlyList<ModuleEntry> loadOrder)
    {
        ArgumentNullException.ThrowIfNull(loadOrder);

        var schemas = SchemasFolder(loadOrder);
        var faults = new List<XmlMergeFault>();
        var cache = new Schemas();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // A dataset whose file list is the same under both campaign game types merges the same way under
        // both, so it is walked once. Only a dataset some module restricted to one of them is walked
        // twice, which on a large install is a handful rather than all of them.
        var walked = new HashSet<string>(StringComparer.Ordinal);
        var datasets = 0;
        var files = 0;

        foreach (var gameType in CampaignGameTypes)
        {
            var pipeline = Build(loadOrder, schemas, gameType);

            foreach (var (dataset, slots) in pipeline.Datasets)
            {
                if (!walked.Add($"{dataset}|{string.Join("|", slots.Select(s => s.XmlPath))}"))
                    continue;

                datasets++;
                files += slots.Count(s => s.XmlPath.Length > 0);

                foreach (var fault in Run(dataset, slots, pipeline.Read, cache))
                {
                    // The same fault under both campaign game types is one fault. The key deliberately
                    // leaves the game type out for that reason and keeps the entry, because two entries
                    // failing the same way are two things the user has to fix.
                    if (seen.Add($"{fault.Kind}|{fault.Dataset}|{fault.ElementPath}|{fault.EntryPath}"))
                        faults.Add(fault);
                }
            }
        }

        return new XmlMergeFaultReport(datasets, files, faults);
    }

    // One entry of the two parallel lists GetMergedXmlForManaged builds, with the schema path it chose
    // for this module carried alongside. Two modules contributing to one dataset can be validated and
    // merged against different schemas, because GetXsdPathForModules prefers the module's own.
    private sealed record Slot(
        ModuleId ModuleId,
        string DisplayName,
        string XmlPath,
        string SchemaPath,
        string StylesheetPath,
        string Registered);

    private sealed record Pipeline(
        IReadOnlyList<(string Dataset, IReadOnlyList<Slot> Slots)> Datasets,
        IReadOnlySet<string> Read);

    // XmlResource.GetXmlListAndApply, for every enabled module in load order. It reads a schema into
    // XsdElementDictionary only for ModuleHelper.GetXsdPath, which is the game's own XmlSchemas folder,
    // and only when that file exists. Nothing anywhere reads a module's own schema into it.
    private static Pipeline Build(IReadOnlyList<ModuleEntry> loadOrder, string? schemas, string gameType)
    {
        var order = new List<string>();
        var slots = new Dictionary<string, List<Slot>>(StringComparer.Ordinal);
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in loadOrder)
        {
            if (!entry.IsEnabled || entry.Manifest is null)
                continue;

            foreach (var (dataset, name, types) in Registrations(entry.Manifest.ManifestPath))
            {
                if (GameSchema(schemas, dataset) is { } shipped && File.Exists(shipped))
                    read.Add(shipped);

                if (types.Count > 0 && !types.Contains(gameType))
                    continue;

                if (!Merged.Contains(dataset))
                    continue;

                if (!slots.TryGetValue(dataset, out var queued))
                {
                    slots[dataset] = queued = [];
                    order.Add(dataset);
                }

                queued.AddRange(Resolve(entry, dataset, name, schemas));
            }
        }

        return new Pipeline([.. order.Select(d => (d, (IReadOnlyList<Slot>)slots[d]))], read);
    }

    // GetMergedXmlForManaged's three branches, in its order. The third is the one that matters here: a
    // registration with nothing behind it still takes a slot, and that slot is fatal at position zero
    // and ignored everywhere else.
    private static IEnumerable<Slot> Resolve(ModuleEntry entry, string dataset, string name, string? schemas)
    {
        var folder = entry.Manifest!.FolderPath;
        string target;
        string schema;

        try
        {
            target = Path.Combine(folder, "ModuleData", Relative(name));

            var own = Path.Combine(folder, "ModuleData", "XmlSchemas", dataset + ".xsd");

            schema = File.Exists(own) ? own : GameSchema(schemas, dataset) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            yield break;
        }

        Slot At(string xml, string stylesheet) =>
            new(entry.Id, entry.DisplayName, xml, schema, stylesheet, Registered(folder, name));

        if (File.Exists(target + ".xml"))
        {
            yield return At(target + ".xml", Stylesheet(target));
            yield break;
        }

        if (Directory.Exists(target))
        {
            string[] found;

            try
            {
                found = Directory.GetFiles(target, "*.xml", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                yield break;
            }

            foreach (var file in found)
                yield return At(file, Stylesheet(file[..^4]));

            yield break;
        }

        yield return At(string.Empty, Stylesheet(target));
    }

    // CreateMergedXmlFile, with the two dictionary lookups MergeElements performs left unguarded in
    // spirit and recorded rather than thrown in practice. The engine stops at the first one; carrying on
    // is what turns "your game crashes" into the whole list of what has to change for it not to.
    private static IEnumerable<XmlMergeFault> Run(
        string dataset,
        IReadOnlyList<Slot> slots,
        IReadOnlySet<string> read,
        Schemas cache)
    {
        if (slots.Count == 0)
            yield break;

        if (slots[0].XmlPath.Length == 0)
        {
            yield return new XmlMergeFault(XmlMergeFaultKind.FirstFileMissing, dataset, slots[0].SchemaPath,
                string.Empty, string.Empty, [], Contributor(slots[0]), 0);
            yield break;
        }

        var merged = Load(slots[0].XmlPath);

        if (merged is null)
            yield break;

        for (var i = 1; i < slots.Count; i++)
        {
            var slot = slots[i];

            if (slot.StylesheetPath.Length > 0)
            {
                try
                {
                    merged = XmlDatasetOverlaps.RunStylesheet(slot.StylesheetPath, merged);
                }
                // A stylesheet BEM cannot run is XmlDatasetOverlaps' finding to report, not this one's.
                // What matters here is that the document carries on from where the engine would have it,
                // and the untransformed document is the closest available.
                catch (Exception)
                {
                    // Intentionally left where it was.
                }
            }

            if (slot.XmlPath.Length == 0)
                continue;

            if (slot.SchemaPath.Length == 0 || !read.Contains(slot.SchemaPath))
            {
                yield return new XmlMergeFault(XmlMergeFaultKind.SchemaNeverRead, dataset, slot.SchemaPath,
                    string.Empty, string.Empty, [], Contributor(slot), i);
                yield break;
            }

            var schema = cache.Read(slot.SchemaPath);
            var incoming = Load(slot.XmlPath);

            if (incoming is null)
                continue;

            var found = new List<(string ElementPath, string EntryPath)>();

            MergeElements(merged, incoming, $"/{merged.Name}", string.Empty, schema, found);

            // The culprit is looked up once per element path rather than once per entry. The same
            // malformed shape usually spans several entries, and each lookup opens every file the
            // dataset has.
            var blamed = new Dictionary<string, IReadOnlyList<XmlMergeContributor>>(StringComparer.Ordinal);

            foreach (var (elementPath, entryPath) in found)
            {
                if (!blamed.TryGetValue(elementPath, out var sources))
                    blamed[elementPath] = sources = Sources(slots, i, elementPath);

                yield return new XmlMergeFault(XmlMergeFaultKind.UndeclaredElementPath, dataset,
                    slot.SchemaPath, elementPath, entryPath, sources, Contributor(slot), i);
            }
        }
    }

    // MBObjectManager.MergeElements, followed line for line, with the schema lookup the engine leaves
    // unguarded recorded instead of thrown. The engine indexes elementSchema in three places and all
    // three key on an element of the merged document taken from this same lookup, so one check covers
    // the lot: if the first of a name group is missing, so is every later index of it.
    private static void MergeElements(
        XElement into,
        XElement from,
        string path,
        string entry,
        Schema schema,
        List<(string ElementPath, string EntryPath)> found)
    {
        var replace = from.Attributes().Any(a => a.Name.LocalName == "_replaceWhileMerging" && a.Value == "true");

        if (replace)
            into.RemoveAttributes();

        foreach (var attribute in from.Attributes())
            into.SetAttributeValue(attribute.Name, attribute.Value);

        if (into.Value.Length > 0 && from.Value.Length > 0)
            into.Value = from.Value;

        if (replace)
            into.Elements().Remove();

        var byName = new Dictionary<XName, Group>();

        foreach (var child in into.Elements())
        {
            var childPath = $"{path}/{child.Name}";

            if (!byName.TryGetValue(child.Name, out var group))
            {
                byName[child.Name] = group = new Group(child, schema.Declares(childPath));

                if (!group.Declared)
                    found.Add((childPath, entry));
            }

            group.Keyed[Key(child, schema.UniqueAttributes(childPath))] = child;
        }

        foreach (var child in from.Elements())
        {
            var childPath = $"{path}/{child.Name}";
            var childEntry = Entry(entry, child);

            // An element the schema does not declare is where the engine stopped. Recursing past it
            // would report faults on a document the game never built, so the walk goes no deeper here
            // and the merge carries on beside it.
            if (byName.TryGetValue(child.Name, out var group) && group.Declared)
            {
                if (schema.PrefersMerge(childPath))
                {
                    MergeElements(group.First, child, childPath, childEntry, schema, found);
                    continue;
                }

                var key = Key(child, schema.UniqueAttributes(childPath));

                if (key.Length > 0 && group.Keyed.TryGetValue(key, out var match))
                {
                    MergeElements(match, child, childPath, childEntry, schema, found);
                    continue;
                }

                if (child.Attributes().Any(a => a.Name.LocalName == "_replaceWhileMerging" && a.Value == "true"))
                {
                    MergeElements(group.First, child, childPath, childEntry, schema, found);
                    continue;
                }
            }

            into.Add(new XElement(child));
        }
    }

    private sealed class Group(XElement first, bool declared)
    {
        public XElement First { get; } = first;

        public bool Declared { get; } = declared;

        public Dictionary<string, XElement> Keyed { get; } = new(StringComparer.Ordinal);
    }

    // Which modules put this element there, found by opening their files and looking. Reading it off
    // the merged document would mean trusting BEM's own bookkeeping about who added what; the files are
    // evidence. Both halves of a slot count, because a stylesheet writing a literal element the schema
    // has never heard of is exactly as fatal as an XML file shipping one, and is harder to spot by eye.
    private static IReadOnlyList<XmlMergeContributor> Sources(
        IReadOnlyList<Slot> slots,
        int upto,
        string elementPath)
    {
        var steps = elementPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (steps.Length == 0)
            return [];

        var leaf = steps[^1];
        var sources = new List<XmlMergeContributor>();

        // Only what the engine had already merged can be in the document it choked on. A later module
        // shipping the same shape has not contributed it yet and is not why this one stopped.
        for (var slot = 0; slot < upto && slot < slots.Count; slot++)
        {
            if (Ships(slots[slot].XmlPath, steps))
            {
                sources.Add(Contributor(slots[slot]));
                continue;
            }

            if (Writes(slots[slot].StylesheetPath, leaf))
            {
                sources.Add(new XmlMergeContributor(
                    slots[slot].ModuleId, slots[slot].DisplayName, slots[slot].StylesheetPath));
            }
        }

        return sources;
    }

    private static bool Ships(string path, string[] steps)
    {
        if (path.Length == 0 || Load(path) is not { } root || root.Name.ToString() != steps[0])
            return false;

        var level = new List<XElement> { root };

        for (var i = 1; i < steps.Length && level.Count > 0; i++)
            level = [.. level.SelectMany(e => e.Elements()).Where(e => e.Name.ToString() == steps[i])];

        return level.Count > 0;
    }

    // A stylesheet builds its output out of literal result elements, so an element named in it that is
    // not an XSLT instruction is an element it writes. That is how setAttribute, which looks like an
    // instruction and is not one, ends up in a document as a real element nobody's schema declares.
    private static bool Writes(string path, string leaf)
    {
        if (path.Length == 0 || Load(path) is not { } root)
            return false;

        return root.Descendants().Any(e => e.Name.LocalName == leaf && e.Name.NamespaceName != Xsl);
    }

    private const string Xsl = "http://www.w3.org/1999/XSL/Transform";

    private static XmlMergeContributor Contributor(Slot slot) =>
        new(slot.ModuleId, slot.DisplayName, slot.XmlPath.Length > 0 ? slot.XmlPath : slot.Registered);

    private static string Entry(string entry, XElement element)
    {
        var id = element.Attribute("id")?.Value;

        if (string.IsNullOrWhiteSpace(id))
            return entry;

        var step = $"{element.Name} id=\"{id}\"";

        return entry.Length == 0 ? step : $"{entry} > {step}";
    }

    private static string Key(XElement element, IReadOnlyList<string> unique)
    {
        if (unique.Count == 0)
            return string.Empty;

        if (unique.Count == 1)
            return element.Attribute(unique[0])?.Value ?? string.Empty;

        return string.Concat(unique.Select(a => element.Attribute(a)?.Value ?? string.Empty));
    }

    private static XElement? Load(string path)
    {
        try
        {
            return XDocument.Load(path).Root;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Relative(string name) =>
        name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static string Registered(string folder, string name)
    {
        try
        {
            return Path.Combine(folder, "ModuleData", Relative(name)) + ".xml";
        }
        catch (ArgumentException)
        {
            return name;
        }
    }

    private static string Stylesheet(string target)
    {
        if (File.Exists(target + ".xsl"))
            return target + ".xsl";

        return File.Exists(target + ".xslt") ? target + ".xslt" : string.Empty;
    }

    private static string? GameSchema(string? schemas, string dataset)
    {
        if (schemas is null)
            return null;

        try
        {
            return Path.Combine(schemas, dataset + ".xsd");
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

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

    private static IReadOnlyList<(string Dataset, string Name, IReadOnlySet<string> Types)> Registrations(
        string manifestPath)
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

        var registrations = new List<(string, string, IReadOnlySet<string>)>();

        foreach (var node in document.Root.Descendants("XmlNode"))
        {
            var name = node.Element("XmlName");
            var dataset = name?.Attribute("id")?.Value;
            var path = name?.Attribute("path")?.Value;

            if (string.IsNullOrWhiteSpace(dataset) || string.IsNullOrWhiteSpace(path))
                continue;

            var types = node.Element("IncludedGameTypes")?.Elements()
                .Select(t => t.Attribute("value")?.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToHashSet(StringComparer.Ordinal);

            registrations.Add((dataset, path, types ?? []));
        }

        return registrations;
    }

    // XmlResource.ReadXsdFileAndExtractInformation, keyed the way it keys: the full XPath of every
    // xs:element in the file, and the unique attributes hung off the path of each xs:unique and xs:key.
    private sealed record SchemaElement(bool AlwaysPreferMerge, List<string> Unique);

    private sealed class Schema(Dictionary<string, SchemaElement> elements)
    {
        public static Schema Empty { get; } = new([]);

        public bool Declares(string path) => elements.ContainsKey(path);

        public bool PrefersMerge(string path) =>
            elements.TryGetValue(path, out var element) && element.AlwaysPreferMerge;

        public IReadOnlyList<string> UniqueAttributes(string path) =>
            elements.TryGetValue(path, out var element) ? element.Unique : [];
    }

    private sealed class Schemas
    {
        private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";

        private readonly Dictionary<string, Schema> cache = new(StringComparer.OrdinalIgnoreCase);

        public Schema Read(string path)
        {
            if (cache.TryGetValue(path, out var schema))
                return schema;

            return cache[path] = Parse(path);
        }

        private static Schema Parse(string path)
        {
            XDocument document;

            try
            {
                document = XDocument.Load(path);
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                return Schema.Empty;
            }

            var elements = new Dictionary<string, SchemaElement>(StringComparer.Ordinal);

            foreach (var element in document.Descendants(Xs + "element"))
                elements[XsdPath(element)] = new SchemaElement(PrefersMerge(element), []);

            foreach (var key in document.Descendants(Xs + "unique").Concat(document.Descendants(Xs + "key")))
            {
                var selector = key.Element(Xs + "selector")?.Attribute("xpath")?.Value;
                var keyed = XsdPath(key) + "/" + selector;

                foreach (var field in key.Elements(Xs + "field"))
                {
                    var attribute = field.Attribute("xpath")?.Value;

                    // The engine takes Substring(1) with no check at all, so a field with no xpath is a
                    // NullReferenceException there and a schema BEM refuses to model here.
                    if (attribute is not { Length: > 1 } || !elements.TryGetValue(keyed, out var element))
                        continue;

                    element.Unique.Add(attribute[1..]);
                }
            }

            return new Schema(elements);
        }

        private static bool PrefersMerge(XElement element) =>
            element.Element(Xs + "annotation")?.Element(Xs + "appinfo")?.Element("appSpecificNote")
                ?.Value.Trim() == "AlwaysPreferMerge";

        // XmlResource.GetFullXPathOfElement with isXsd true: anything that is not an xs:element is
        // skipped and the path is built from the name or ref of the ones that are.
        private static string XsdPath(XElement element)
        {
            if (element.Name != Xs + "element")
                return element.Parent is null ? string.Empty : XsdPath(element.Parent);

            var name = element.Attribute("name")?.Value ?? element.Attribute("ref")?.Value ?? string.Empty;

            return element.Parent is null ? name : XsdPath(element.Parent) + "/" + name;
        }
    }
}
