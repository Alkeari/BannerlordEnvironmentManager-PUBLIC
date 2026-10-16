using System.Text;
using System.Xml;
using BannerlordEnvironmentManager.Core.Io;

namespace BannerlordEnvironmentManager.Core.Modules;

public sealed record LostManifestElement(string Description, string ParentName, string Text);

public sealed record ManifestRepairPlan(
    string ManifestPath,
    IReadOnlyList<LostManifestElement> Lost,
    string? Problem = null)
{
    public bool HasWork => Lost.Count > 0 && Problem is null;
}

public sealed record ManifestRepairOutcome(int Modules, int Elements, IReadOnlyList<string> Failed);

// The backup beside a manifest is the file as it was before BEM first wrote to it. Where the live
// file no longer has something the backup does, the author's own words can be put back without
// discarding anything written since: only the missing elements are inserted, so a version rewrite
// the user asked for stays exactly as it is.
public static class ManifestElementRepair
{
    private static readonly (string Container, string Entry, string Id)[] DependencyLists =
    [
        ("DependedModules", "DependedModule", "Id"),
        ("DependedModuleMetadatas", "DependedModuleMetadata", "id")
    ];

    public static ManifestRepairPlan Plan(string manifestPath)
    {
        var backupPath = AtomicXmlFile.BackupPathFor(manifestPath);

        if (!File.Exists(manifestPath) || !File.Exists(backupPath))
            return new ManifestRepairPlan(manifestPath, []);

        string liveText;
        string backupText;

        try
        {
            liveText = XmlTextFile.Read(manifestPath).Value;
            backupText = XmlTextFile.Read(backupPath).Value;
        }
        catch (IOException ex)
        {
            return new ManifestRepairPlan(manifestPath, [], ex.Message);
        }

        List<ScannedElement> live;
        List<ScannedElement> backup;

        try
        {
            live = Scan(liveText);
            backup = Scan(backupText);
        }
        catch (XmlException ex)
        {
            return new ManifestRepairPlan(manifestPath, [], ex.Message);
        }

        var lost = new List<LostManifestElement>();

        foreach (var element in backup.Where(e => e.Depth == 1))
        {
            if (live.Any(e => e.Depth == 1 && e.Name == element.Name))
                continue;

            lost.Add(new LostManifestElement(element.Name, element.ParentName, backupText[element.Start..element.End]));
        }

        foreach (var (container, entry, id) in DependencyLists)
        {
            if (!live.Any(e => e.Depth == 1 && e.Name == container))
                continue;

            var declared = live
                .Where(e => e.Depth == 2 && e.ParentName == container && e.Name == entry)
                .Select(e => e.Id ?? string.Empty)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var element in backup.Where(e => e.Depth == 2 && e.ParentName == container && e.Name == entry))
            {
                if (declared.Contains(element.Id ?? string.Empty))
                    continue;

                lost.Add(new LostManifestElement(
                    $"{entry} {element.Id}",
                    container,
                    backupText[element.Start..element.End]));
            }
        }

        return new ManifestRepairPlan(manifestPath, lost);
    }

    public static IReadOnlyList<ManifestRepairPlan> PlanAll(IEnumerable<string> manifestPaths)
    {
        ArgumentNullException.ThrowIfNull(manifestPaths);

        return [.. manifestPaths.Select(Plan).Where(plan => plan.HasWork)];
    }

    public static ManifestRepairOutcome RepairAll(IEnumerable<ManifestRepairPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);

        var modules = 0;
        var elements = 0;
        var failed = new List<string>();

        foreach (var plan in plans)
        {
            try
            {
                var restored = Repair(plan);

                if (restored == 0)
                    continue;

                modules++;
                elements += restored;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException)
            {
                failed.Add(plan.ManifestPath);
            }
        }

        return new ManifestRepairOutcome(modules, elements, failed);
    }

    public static int Repair(ManifestRepairPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.HasWork)
            return 0;

        var file = XmlTextFile.Read(plan.ManifestPath);
        var text = file.Value;
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var restored = 0;

        foreach (var group in plan.Lost.GroupBy(element => element.ParentName, StringComparer.Ordinal))
        {
            var parent = Scan(text).FirstOrDefault(e => e.Depth <= 1 && e.Name == group.Key);

            if (parent is null)
                continue;

            text = Insert(text, parent, [.. group.Select(element => element.Text.ReplaceLineEndings(newLine))], newLine);
            restored += group.Count();
        }

        if (restored == 0)
            return 0;

        // A repair that produced something the game cannot read would be worse than the loss it fixes.
        Scan(text);

        AtomicXmlFile.Save(file.ToBytes(text), plan.ManifestPath, writeBackup: false);
        return restored;
    }

    private static string Insert(string text, ScannedElement parent, IReadOnlyList<string> additions, string newLine)
    {
        var builder = new StringBuilder(text.Length);

        if (parent.CloseTagStart < 0)
        {
            // An empty <DependedModules /> has nowhere to put an entry, so it is opened first.
            var indent = IndentOf(text, parent.Start);

            builder.Append(text[..text.LastIndexOf('/', parent.End - 1)].TrimEnd());
            builder.Append('>').Append(newLine);
            AppendAdditions(builder, additions, newLine, indent);
            builder.Append(indent).Append("</").Append(parent.Name).Append('>');
            builder.Append(text, parent.End, text.Length - parent.End);

            return builder.ToString();
        }

        var closeIndent = IndentOf(text, parent.CloseTagStart);
        var insertAt = parent.CloseTagStart - closeIndent.Length;

        builder.Append(text, 0, insertAt);
        AppendAdditions(builder, additions, newLine, closeIndent);
        builder.Append(text, insertAt, text.Length - insertAt);

        return builder.ToString();
    }

    private static void AppendAdditions(StringBuilder builder, IReadOnlyList<string> additions, string newLine, string indent)
    {
        foreach (var addition in additions)
            builder.Append(indent).Append("  ").Append(addition).Append(newLine);
    }

    private static string IndentOf(string text, int offset)
    {
        var start = offset;

        while (start > 0 && text[start - 1] is ' ' or '\t')
            start--;

        return text[start..offset];
    }

    private sealed record ScannedElement(string Name, string ParentName, int Depth, string? Id, int Start)
    {
        public int End { get; set; }

        public int CloseTagStart { get; set; } = -1;
    }

    // Only the two levels the repair works at are scanned: the manifest's own sections and the
    // dependency entries inside them.
    private static List<ScannedElement> Scan(string xml)
    {
        var lineStarts = LineStarts(xml);
        var scanned = new List<ScannedElement>();
        var open = new Stack<ScannedElement?>();
        var path = new List<string>();
        var pending = new List<ScannedElement>();

        using var reader = XmlReader.Create(
            new StringReader(xml),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = false });

        var lineInfo = (IXmlLineInfo)reader;

        while (reader.Read())
        {
            var reported = Offset(lineStarts, lineInfo);

            if (pending.Count > 0)
            {
                var end = EndOfPreviousNode(xml, reported);

                foreach (var element in pending)
                    element.End = end;

                pending.Clear();
            }

            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    var depth = path.Count;
                    var name = reader.Name;
                    var tracked = depth <= 2 ? Track(reader, scanned, path, depth, xml.LastIndexOf('<', reported)) : null;

                    if (reader.IsEmptyElement)
                    {
                        if (tracked is not null)
                            pending.Add(tracked);

                        break;
                    }

                    open.Push(tracked);
                    path.Add(name);
                    break;

                case XmlNodeType.EndElement:
                    path.RemoveAt(path.Count - 1);

                    if (open.Pop() is { } closed)
                    {
                        closed.CloseTagStart = xml.LastIndexOf('<', reported);
                        pending.Add(closed);
                    }

                    break;
            }
        }

        foreach (var element in pending)
            element.End = EndOfPreviousNode(xml, xml.Length - 1);

        return scanned;
    }

    private static ScannedElement? Track(XmlReader reader, List<ScannedElement> scanned, List<string> path, int depth, int start)
    {
        var element = new ScannedElement(
            reader.Name,
            depth == 0 ? string.Empty : path[^1],
            depth,
            reader.GetAttribute("Id") ?? reader.GetAttribute("id"),
            start);

        reader.MoveToElement();
        scanned.Add(element);

        return element;
    }

    // The reported position of a node is inside its opening delimiter, so stepping back over the
    // delimiter and the whitespace before it lands on the last character of the node before it.
    private static int EndOfPreviousNode(string xml, int reported)
    {
        var index = Math.Min(reported, xml.Length - 1);

        while (index > 0 && (char.IsWhiteSpace(xml[index]) || xml[index] is '<' or '/'))
            index--;

        return index + 1;
    }

    private static int Offset(IReadOnlyList<int> lineStarts, IXmlLineInfo lineInfo)
    {
        var line = lineInfo.LineNumber - 1;

        return line < 0 || line >= lineStarts.Count ? 0 : lineStarts[line] + lineInfo.LinePosition - 1;
    }

    private static List<int> LineStarts(string xml)
    {
        var starts = new List<int> { 0 };

        for (var i = 0; i < xml.Length; i++)
        {
            if (xml[i] == '\n')
                starts.Add(i + 1);
            else if (xml[i] == '\r' && (i + 1 == xml.Length || xml[i + 1] != '\n'))
                starts.Add(i + 1);
        }

        return starts;
    }
}
