using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.DryRun;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// One kind of ModuleData XML element a game method reads, with the ids it is known to read (empty when it
// reads every element of that kind).
public sealed record GameDataElement(string Element, IReadOnlyList<string> Ids);

public sealed record GameMethodDataRead(string Type, string Method, IReadOnlyList<GameDataElement> Reads);

// A game method that crashed with no mod frame on its stack still read XML a mod may have rewritten.
// This table names what each such method reads, and the matcher lists the enabled modules whose
// ModuleData XML or XSLT touches those elements, each with the file. It is where to look, never a verdict.
public static partial class GameDataFeeds
{
    public const int IdScore = 2;

    public const int ElementScore = 1;

    // A file the engine log shows failing the schema for the data the method reads outranks any text match.
    public const int SchemaScore = 10;

    // Without a named id among the missing objects, this many missing objects still count as the load failing.
    private const int ManyNulls = 20;

    public static IReadOnlyList<GameMethodDataRead> Table { get; } =
    [
        new("Campaign", "InitializeDefaultEquipments",
        [
            new("EquipmentRoster",
            [
                "default_battle_equipment_roster_neutral",
                "default_civilian_equipment_roster_neutral",
                "default_stealth_equipment_roster",
            ]),
            new("NPCCharacter", []),
        ]),
    ];

    public static GameMethodDataRead? Find(string type, string method)
    {
        var shortType = Short(type);
        var baseMethod = PatchSuffix().Replace(method, string.Empty);

        return Table.FirstOrDefault(t => t.Type == shortType && t.Method == baseMethod);
    }

    // Replaces the involved list with the data-feed suspects when the stack names no mod (BEM's own
    // companion is not one).
    public static StackAttribution Extend(StackAttribution attribution, IEnumerable<ModuleEntry> entries, EngineLogFindings? log = null)
    {
        ArgumentNullException.ThrowIfNull(attribution);

        if (attribution.Involved.Any(m => !CompanionManifest.IsCompanionId(m.Module)))
            return attribution;

        var suspects = Suspects(attribution.Frames, entries, log);

        return suspects.Count == 0 ? attribution : attribution with { Involved = suspects };
    }

    public static IReadOnlyList<InvolvedModule> Suspects(
        IEnumerable<AttributedFrame> frames, IEnumerable<ModuleEntry> entries, EngineLogFindings? log = null)
    {
        var reads = frames
            .Select(f => Find(f.Type, f.Method))
            .Where(r => r is not null)
            .SelectMany(r => r!.Reads)
            .GroupBy(r => r.Element, StringComparer.Ordinal)
            .Select(g => new GameDataElement(g.Key, [.. g.SelectMany(r => r.Ids).Distinct(StringComparer.Ordinal)]))
            .ToList();

        if (reads.Count == 0)
            return [];

        var result = new List<InvolvedModule>();
        var entryList = entries.ToList();
        var fromLog = EngineSuspects(reads, entryList, log);

        foreach (var entry in entryList.Where(e => e.IsEnabled && e.Manifest is { IsOfficial: false }))
        {
            var folder = Path.Combine(entry.Manifest!.FolderPath, "ModuleData");

            if (!Directory.Exists(folder))
                continue;

            var hits = new List<(string File, int Score)>();

            foreach (var file in EnumerateData(folder))
            {
                var text = TryRead(file);

                if (text is null)
                    continue;

                var score = 0;

                foreach (var read in reads)
                {
                    if (!ElementPattern(read.Element).IsMatch(text))
                        continue;

                    score = Math.Max(score, read.Ids.Any(id => text.Contains(id, StringComparison.Ordinal)) ? IdScore : ElementScore);
                }

                if (score > 0)
                    hits.Add((Path.GetRelativePath(entry.Manifest.FolderPath, file), score));
            }

            if (hits.Count == 0 || fromLog.ContainsKey(entry.Id.Value))
                continue;

            var ordered = hits.OrderByDescending(h => h.Score).ThenBy(h => h.File, StringComparer.OrdinalIgnoreCase).ToList();

            result.Add(new InvolvedModule(entry.Id.Value, ordered.Count, ordered.Max(h => h.Score), FrameSource.DataFeed)
            {
                Files = [.. ordered.Select(h => h.File)],
            });
        }

        result.AddRange(fromLog.Values);

        return [.. result.OrderByDescending(m => m.Score).ThenByDescending(m => m.Frames).ThenBy(m => m.Module, StringComparer.OrdinalIgnoreCase)];
    }

    // The modules whose data file of a kind the method reads failed the game's schema check, when the
    // missing objects show the load was lost: the read ids among them, or a great many of them. A file of
    // another kind that fails the schema (a weapon description) proves nothing about this crash.
    private static Dictionary<string, InvolvedModule> EngineSuspects(
        List<GameDataElement> reads, List<ModuleEntry> entries, EngineLogFindings? log)
    {
        var found = new Dictionary<string, InvolvedModule>(StringComparer.OrdinalIgnoreCase);

        if (log is null || log.SchemaErrors.Count == 0)
            return found;

        var missing = new HashSet<string>(log.NullIds, StringComparer.Ordinal);

        foreach (var read in reads)
        {
            var named = read.Ids.Where(missing.Contains).ToList();

            if (named.Count == 0 && log.NullIds.Count < ManyNulls)
                continue;

            foreach (var error in log.SchemaErrors.Where(e => string.Equals(e.Schema, read.Element + "s.xsd", StringComparison.OrdinalIgnoreCase)))
            {
                var entry = entries.FirstOrDefault(e => e.IsEnabled && e.Manifest is { IsOfficial: false }
                    && string.Equals(Path.GetFileName(e.Manifest.FolderPath.TrimEnd('\\', '/')), error.ModuleFolder, StringComparison.OrdinalIgnoreCase));

                if (entry is null || found.ContainsKey(entry.Id.Value))
                    continue;

                found[entry.Id.Value] = new InvolvedModule(entry.Id.Value, 1, SchemaScore, FrameSource.EngineLog)
                {
                    Files = [error.File],
                    Engine = new EngineEvidence(error.File, error.ErrorCount, read.Element, named, log.NullIds.Count),
                };
            }
        }

        return found;
    }

    private static List<string> EnumerateData(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xslt", StringComparison.OrdinalIgnoreCase))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? TryRead(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The element as an XML tag or an XPath step: <Name, /Name or Name[.
    private static Regex ElementPattern(string element) =>
        new(@"(?:<|/)" + Regex.Escape(element) + @"(?![\w])|(?<![\w])" + Regex.Escape(element) + @"\[", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"_Patch\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex PatchSuffix();

    private static string Short(string type)
    {
        var cut = type.LastIndexOfAny(['.', '+', '/']);

        return cut < 0 ? type : type[(cut + 1)..];
    }
}
