using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BannerlordEnvironmentManager.Core.LoadOrder;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public sealed record GraftedAction(string ActionSetId, string ActionType, string AnimationName);

public sealed record StubAnimation(ModuleId ModuleId, string Path, string AnimationName, string SourceClipName, long SizeBytes);

public sealed record RigPatcher(
    ModuleId ModuleId,
    string DisplayName,
    int LoadOrderIndex,
    bool UsesXslt,
    IReadOnlyList<GraftedAction> Actions,
    IReadOnlyList<StubAnimation> StubAnimations);

public sealed record RigConflict(
    string ActionSetId,
    IReadOnlyList<RigPatcher> Patchers,
    IReadOnlyList<RigPatcher> AlsoWriting)
{
    // Modules are merged in load order, so the last one to write this action set is the one whose
    // entries land last. These two append rather than overwrite, so both survive; the winner only
    // decides which definition stands if they ever do collide on the same action type.
    public RigPatcher AppliedLast => Patchers.MaxBy(p => p.LoadOrderIndex)!;
}

public enum RigOrderStatus
{
    NoRegistrants,
    AnchorFirst,
    AnchorMissing,
    AnchorDisplaced
}

// MBObjectManager.CreateMergedXmlFile applies xsltList[1..] only, so whatever registers a soln_action_*
// id first in load order has its XSLT dropped and its XML used as the document everything else merges
// into. That first slot belongs to the module shipping the base data, which is Native.
public sealed record RigOrderFix(
    RigOrderStatus Status,
    ModuleId? AnchorId,
    IReadOnlyList<ModuleId> Registrants,
    IReadOnlyList<ModuleId> AboveAnchor,
    IReadOnlyList<ModuleEntry> Corrected)
{
    public bool IsNeeded => Status is RigOrderStatus.AnchorDisplaced;
}

public sealed record RigReport(
    IReadOnlyList<RigPatcher> Patchers,
    IReadOnlyList<RigConflict> Conflicts,
    IReadOnlyList<StubAnimation> StubAnimations,
    RigOrderFix? OrderFix = null);

public static partial class RigConflicts
{
    private const int StubSizeLimitBytes = 4096;

    private static readonly string[] RigSolutionIds = ["soln_action_sets", "soln_action_types"];

    // The clips vanilla plays only inside character creation and the inventory doll, where the pose is
    // driven by the UI rather than by the world. An in-world agent handed one of these stands in a
    // degenerate pose, which is the folded-body appearance.
    private static readonly HashSet<string> FacegenClips = new(StringComparer.OrdinalIgnoreCase)
    {
        "male_custom",
        "female_custom",
        "anim_male_custom",
        "anim_female_custom",
        "anim_female_custom_start",
        "anim_male_custom_voice8"
    };

    public static RigReport Inspect(IReadOnlyList<ModuleEntry> loadOrder)
    {
        ArgumentNullException.ThrowIfNull(loadOrder);

        var patchers = new List<RigPatcher>();
        var stubs = new List<StubAnimation>();

        for (var i = 0; i < loadOrder.Count; i++)
        {
            var entry = loadOrder[i];

            // Native writes the document every other module is merged into, and a module the user has
            // switched off never reaches the engine at all.
            if (!entry.IsEnabled || entry.IsOfficial || entry.Manifest is null)
                continue;

            var folder = entry.Manifest.FolderPath;
            var moduleStubs = ReadStubAnimations(entry.Id, folder);
            stubs.AddRange(moduleStubs);

            var registrations = ReadRigRegistrations(folder);

            if (registrations.Count == 0)
                continue;

            var actions = new List<GraftedAction>();
            var usesXslt = false;
            var shipsRigData = false;

            foreach (var registration in registrations)
            {
                foreach (var path in RigDataPaths(folder, registration))
                {
                    if (!File.Exists(path))
                        continue;

                    shipsRigData = true;

                    var isXslt = !path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
                    usesXslt |= isXslt;

                    actions.AddRange(isXslt ? ReadXsltActions(path) : ReadXmlActions(path));
                }
            }

            if (!shipsRigData)
                continue;

            patchers.Add(new RigPatcher(
                entry.Id,
                entry.DisplayName,
                i,
                usesXslt,
                Distinct(actions),
                moduleStubs));
        }

        return new RigReport(patchers, FindConflicts(patchers), stubs, PlanNativeFirst(loadOrder));
    }

    // The only part of the folded-body remedy BEM may apply on its own: it moves nothing on disk, changes
    // no module's enabled state, and enforces a rule the engine's own merge code makes mandatory.
    public static RigOrderFix PlanNativeFirst(IReadOnlyList<ModuleEntry> loadOrder)
    {
        ArgumentNullException.ThrowIfNull(loadOrder);

        var registrants = new List<(int Index, ModuleEntry Entry)>();
        var anchorIndex = -1;

        for (var i = 0; i < loadOrder.Count; i++)
        {
            var entry = loadOrder[i];

            if (!entry.IsEnabled || entry.Manifest is null)
                continue;

            var registrations = ReadRigRegistrations(entry.Manifest.FolderPath);

            if (registrations.Count == 0)
                continue;

            if (!entry.IsOfficial)
            {
                registrants.Add((i, entry));
                continue;
            }

            // The anchor has to be the module carrying the base document, not merely an official module
            // that mentions the id, or the merge would start from an XSLT with nothing to transform.
            if (anchorIndex < 0 && ShipsRigXml(entry.Manifest.FolderPath, registrations))
                anchorIndex = i;
        }

        var ids = registrants.Select(r => r.Entry.Id).ToList();

        if (registrants.Count == 0)
            return new RigOrderFix(RigOrderStatus.NoRegistrants, null, ids, [], loadOrder);

        if (anchorIndex < 0)
            return new RigOrderFix(RigOrderStatus.AnchorMissing, null, ids, [], loadOrder);

        var anchor = loadOrder[anchorIndex];
        var above = registrants.Where(r => r.Index < anchorIndex).ToList();

        if (above.Count == 0)
            return new RigOrderFix(RigOrderStatus.AnchorFirst, anchor.Id, ids, [], loadOrder);

        // Moving the anchor up to the first displaced registrant keeps every other module where the user
        // put it, which a full re-sort would not.
        var corrected = new List<ModuleEntry>(loadOrder);
        corrected.RemoveAt(anchorIndex);
        corrected.Insert(above[0].Index, anchor);

        return new RigOrderFix(
            RigOrderStatus.AnchorDisplaced,
            anchor.Id,
            ids,
            [.. above.Select(r => r.Entry.Id)],
            corrected);
    }

    private static bool ShipsRigXml(string folder, IReadOnlyList<string> registrations) =>
        registrations.Any(registration => RigDataPaths(folder, registration)
            .Any(path => path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && File.Exists(path)));

    private static IReadOnlyList<RigConflict> FindConflicts(IReadOnlyList<RigPatcher> patchers)
    {
        var conflicts = new List<RigConflict>();

        var byActionSet = patchers
            .SelectMany(p => p.Actions.Select(a => a.ActionSetId).Distinct(StringComparer.Ordinal), (p, id) => (Id: id, Patcher: p))
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in byActionSet)
        {
            var writers = group.Select(x => x.Patcher).OrderBy(p => p.LoadOrderIndex).ToList();

            // Four enabled modules append to as_human_warrior on the reference install and only two are
            // behind the bug, so the overlap alone is half noise. What separates them is that their
            // grafted actions are backed by definition-only animation stubs rather than by real clips.
            var stubBacked = writers.Where(p => IsStubBacked(p, group.Key)).ToList();

            if (stubBacked.Count < 2)
                continue;

            conflicts.Add(new RigConflict(
                group.Key,
                stubBacked,
                [.. writers.Except(stubBacked)]));
        }

        return conflicts;
    }

    private static bool IsStubBacked(RigPatcher patcher, string actionSetId) =>
        patcher.Actions.Any(action =>
            string.Equals(action.ActionSetId, actionSetId, StringComparison.Ordinal)
            && patcher.StubAnimations.Any(stub =>
                string.Equals(stub.AnimationName, action.AnimationName, StringComparison.OrdinalIgnoreCase)));

    private static IReadOnlyList<GraftedAction> Distinct(IEnumerable<GraftedAction> actions) =>
        [.. actions.GroupBy(a => (a.ActionSetId, a.ActionType), TupleComparer.Instance).Select(g => g.First())];

    private sealed class TupleComparer : IEqualityComparer<(string ActionSetId, string ActionType)>
    {
        public static TupleComparer Instance { get; } = new();

        public bool Equals((string ActionSetId, string ActionType) x, (string ActionSetId, string ActionType) y) =>
            string.Equals(x.ActionSetId, y.ActionSetId, StringComparison.Ordinal)
            && string.Equals(x.ActionType, y.ActionType, StringComparison.Ordinal);

        public int GetHashCode((string ActionSetId, string ActionType) obj) =>
            HashCode.Combine(obj.ActionSetId, obj.ActionType);
    }

    // XmlResource.GetMbprojxmls reads only <file> children of <base>. A template copied with <Module>
    // elements registers nothing, and an XML parse drops a commented-out registration for free, both of
    // which the reference install actually contains.
    private static IReadOnlyList<string> ReadRigRegistrations(string moduleFolder)
    {
        var path = Path.Combine(moduleFolder, "ModuleData", "project.mbproj");

        if (!File.Exists(path))
            return [];

        XDocument document;

        try
        {
            document = XDocument.Load(path);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return [];
        }

        if (document.Root is null || document.Root.Name.LocalName != "base")
            return [];

        return
        [
            .. document.Root.Elements("file")
                .Where(e => RigSolutionIds.Contains(e.Attribute("id")?.Value, StringComparer.Ordinal))
                .Select(e => e.Attribute("name")?.Value)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];
    }

    // ModuleHelper.GetXsltPath swaps the registered file's extension for .xsl, and
    // MBObjectManager.HandleXsltList retries with a trailing t, so all three spellings are live.
    private static IEnumerable<string> RigDataPaths(string moduleFolder, string registeredName)
    {
        var relative = registeredName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string full;

        try
        {
            full = Path.Combine(moduleFolder, relative);
        }
        catch (ArgumentException)
        {
            yield break;
        }

        yield return full;
        yield return Path.ChangeExtension(full, ".xsl");
        yield return Path.ChangeExtension(full, ".xslt");
    }

    private static IReadOnlyList<GraftedAction> ReadXmlActions(string path)
    {
        var root = TryLoad(path);

        if (root is null)
            return [];

        return
        [
            .. root.Descendants()
                .Where(e => e.Name.LocalName == "action_set" && e.Attribute("id") is not null)
                .SelectMany(set => set.Descendants()
                    .Where(e => e.Name.LocalName == "action")
                    .Select(action => Grafted(set.Attribute("id")!.Value, action)))
                .Where(a => a is not null)
                .Select(a => a!)
        ];
    }

    private static IReadOnlyList<GraftedAction> ReadXsltActions(string path)
    {
        var root = TryLoad(path);

        if (root is null)
            return [];

        var grafted = new List<GraftedAction>();

        foreach (var template in root.Descendants().Where(e => e.Name.LocalName == "template"))
        {
            var match = template.Attribute("match")?.Value;

            if (match is null)
                continue;

            var actionSet = ActionSetMatch().Match(match);

            if (!actionSet.Success)
                continue;

            foreach (var action in template.Descendants().Where(e => e.Name.LocalName == "action"))
            {
                var entry = Grafted(actionSet.Groups[1].Value, action);

                if (entry is not null)
                    grafted.Add(entry);
            }
        }

        return grafted;
    }

    private static GraftedAction? Grafted(string actionSetId, XElement action)
    {
        var type = action.Attribute("type")?.Value;

        return string.IsNullOrWhiteSpace(type)
            ? null
            : new GraftedAction(actionSetId, type, action.Attribute("animation")?.Value ?? string.Empty);
    }

    private static XElement? TryLoad(string path)
    {
        try
        {
            return XDocument.Load(path).Root;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"action_set\[\s*@id\s*=\s*['""]([^'""]+)['""]\s*\]")]
    private static partial Regex ActionSetMatch();

    private static IReadOnlyList<StubAnimation> ReadStubAnimations(ModuleId moduleId, string moduleFolder)
    {
        var stubs = new List<StubAnimation>();

        foreach (var assetFolder in new[] { "Assets", "AssetPackages" })
        {
            var folder = Path.Combine(moduleFolder, assetFolder);

            if (!Directory.Exists(folder))
                continue;

            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles(folder, "*_anm.tpac", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var stub = ReadStub(moduleId, file);

                if (stub is not null)
                    stubs.Add(stub);
            }
        }

        return stubs;
    }

    private static StubAnimation? ReadStub(ModuleId moduleId, string path)
    {
        byte[] bytes;

        try
        {
            var info = new FileInfo(path);

            // A real animation carries keyframes and runs to tens of kilobytes. Anything this small is
            // an asset record with nothing but a name and a pointer to somebody else's clip.
            if (info.Length >= StubSizeLimitBytes)
                return null;

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (bytes.Length < 8 || bytes[0] != (byte)'T' || bytes[1] != (byte)'P' || bytes[2] != (byte)'A' || bytes[3] != (byte)'C')
            return null;

        var identifiers = ReadIdentifiers(bytes);

        if (identifiers.Count < 2 || !FacegenClips.Contains(identifiers[1]))
            return null;

        return new StubAnimation(moduleId, path, identifiers[0], identifiers[1], bytes.Length);
    }

    // The header's numeric fields have no documented layout, so the strings are found positionally
    // instead: a length prefix followed by exactly that many identifier characters. The first is the
    // animation's own name and the second is the clip it points at.
    private static IReadOnlyList<string> ReadIdentifiers(byte[] bytes)
    {
        var found = new List<string>();
        var offset = 8;

        while (offset + 4 < bytes.Length && found.Count < 2)
        {
            var length = BitConverter.ToInt32(bytes, offset);

            if (length is >= 3 and <= 128 && offset + 4 + length <= bytes.Length && IsIdentifier(bytes, offset + 4, length))
            {
                found.Add(Encoding.ASCII.GetString(bytes, offset + 4, length));
                offset += 4 + length;
                continue;
            }

            offset++;
        }

        return found;
    }

    private static bool IsIdentifier(byte[] bytes, int start, int length)
    {
        if (bytes[start] is >= (byte)'0' and <= (byte)'9')
            return false;

        for (var i = start; i < start + length; i++)
        {
            var c = bytes[i];

            if (c is not ((>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9') or (byte)'_'))
                return false;
        }

        return true;
    }
}
