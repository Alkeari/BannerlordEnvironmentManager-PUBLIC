using System.Xml;
using System.Xml.Linq;

namespace BannerlordEnvironmentManager.Core.Install;

public sealed record XmlAttributeChange(string Name, string CurrentValue, string IncomingValue);

public sealed record XmlEntryChange(string Key, IReadOnlyList<XmlAttributeChange> Attributes, bool ContentChanged);

public sealed record XmlMergePlan(
    string RootName,
    string KeyAttribute,
    IReadOnlyList<XmlEntryChange> Changed,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Preserved,
    int CurrentEntryCount,
    int IncomingEntryCount)
{
    public IReadOnlyList<string> ChangedAttributeNames { get; } =
    [
        .. Changed.SelectMany(change => change.Attributes.Select(attribute => attribute.Name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    public bool IsEmpty => Changed.Count == 0 && Added.Count == 0;
}

// An update archive is a whole file built against whatever version its author had. Overwriting drops
// every entry the game has added since; merging by entry name replays only what the author actually
// changed, which is what they would have shipped had they built against the current version.
public static class XmlMerge
{
    private static readonly string[] KeyAttributeCandidates = ["id", "name", "key"];

    public static XmlMergePlan? Plan(string currentXml, string incomingXml)
    {
        if (Parse(currentXml) is not { } current || Parse(incomingXml) is not { } incoming)
            return null;

        if (current.Root is null || incoming.Root is null || current.Root.Name != incoming.Root.Name)
            return null;

        if (ResolveKeyAttribute(current.Root, incoming.Root) is not { } keyAttribute)
            return null;

        var currentEntries = Index(current.Root, keyAttribute);
        var incomingEntries = Index(incoming.Root, keyAttribute);

        if (currentEntries is null || incomingEntries is null)
            return null;

        var changed = new List<XmlEntryChange>();
        var added = new List<string>();

        foreach (var (key, incomingEntry) in incomingEntries)
        {
            if (!currentEntries.TryGetValue(key, out var currentEntry))
            {
                added.Add(key);
                continue;
            }

            var attributes = DiffAttributes(currentEntry, incomingEntry);
            var contentChanged = ContentOf(currentEntry) != ContentOf(incomingEntry);

            if (attributes.Count > 0 || contentChanged)
                changed.Add(new XmlEntryChange(key, attributes, contentChanged));
        }

        return new XmlMergePlan(
            current.Root.Name.LocalName,
            keyAttribute,
            changed,
            added,
            [.. currentEntries.Keys.Where(key => !incomingEntries.ContainsKey(key))],
            currentEntries.Count,
            incomingEntries.Count);
    }

    public static IReadOnlyCollection<string>? EntryKeys(string xml)
    {
        if (Parse(xml) is not { Root: { } root })
            return null;

        foreach (var candidate in KeyAttributeCandidates)
        {
            if (Index(root, candidate) is { } entries)
                return entries.Keys;
        }

        return null;
    }

    public static string Apply(string currentXml, string incomingXml)
    {
        var current = Parse(currentXml) ?? throw new InvalidOperationException("The installed file is not valid XML.");
        var incoming = Parse(incomingXml) ?? throw new InvalidOperationException("The incoming file is not valid XML.");

        var keyAttribute = ResolveKeyAttribute(current.Root!, incoming.Root!)
            ?? throw new InvalidOperationException("The two files have no entry identity in common.");

        if (Splice(currentXml, incomingXml, keyAttribute) is { } spliced)
            return spliced;

        var currentEntries = Index(current.Root!, keyAttribute)!;
        var incomingEntries = Index(incoming.Root!, keyAttribute)!;

        foreach (var (key, incomingEntry) in incomingEntries)
        {
            if (currentEntries.TryGetValue(key, out var currentEntry))
                currentEntry.ReplaceWith(new XElement(incomingEntry));
            else
                current.Root!.Add(new XElement(incomingEntry));
        }

        return Serialize(current, currentXml);
    }

    // Rewriting the document through XLinq would reformat it: XLinq does not model the whitespace
    // between attributes, and Native's flora_kinds.xml puts every attribute on its own line, so a
    // round trip rewrites all 1.5 MB of it. Splicing the incoming entries in as raw text instead leaves
    // every untouched byte of the installed file exactly as the game shipped it, which is what makes
    // the result reviewable in a diff. The result is verified before it is returned.
    private static string? Splice(string currentXml, string incomingXml, string keyAttribute)
    {
        if (Segment(currentXml, keyAttribute) is not { } current || Segment(incomingXml, keyAttribute) is not { } incoming)
            return null;

        var incomingByKey = incoming.Entries.ToDictionary(entry => entry.Key, entry => entry.Element, StringComparer.Ordinal);
        var currentKeys = current.Entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var builder = new System.Text.StringBuilder(currentXml.Length);

        builder.Append(current.Prefix);

        var separator = string.Empty;

        foreach (var entry in current.Entries)
        {
            builder.Append(incomingByKey.TryGetValue(entry.Key, out var replacement) ? replacement : entry.Element);
            builder.Append(entry.Trailing);
            separator = entry.Trailing;
        }

        foreach (var entry in incoming.Entries.Where(entry => !currentKeys.Contains(entry.Key)))
        {
            builder.Append(entry.Element);
            builder.Append(separator);
        }

        builder.Append(current.Suffix);

        var result = builder.ToString();

        return Verify(result, incomingXml, keyAttribute) ? result : null;
    }

    private static bool Verify(string result, string incomingXml, string keyAttribute)
    {
        if (Parse(result) is not { Root: { } root } || Index(root, keyAttribute) is null)
            return false;

        var plan = Plan(result, incomingXml);

        return plan is { Changed.Count: 0, Added.Count: 0 };
    }

    private sealed record RawEntry(string Key, string Element, string Trailing);

    private sealed record RawDocument(string Prefix, IReadOnlyList<RawEntry> Entries, string Suffix);

    private static RawDocument? Segment(string xml, string keyAttribute)
    {
        if (Parse(xml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo) is not { Root: { } root })
            return null;

        var children = root.Elements().ToList();

        if (children.Count == 0)
            return null;

        var lineStarts = LineStarts(xml);
        var starts = new int[children.Count];

        for (var i = 0; i < children.Count; i++)
        {
            if (OffsetOf(children[i], lineStarts, xml) is not { } offset)
                return null;

            starts[i] = offset;
        }

        var closingTag = xml.LastIndexOf($"</{root.Name.LocalName}", StringComparison.Ordinal);

        if (closingTag <= starts[^1])
            return null;

        var entries = new List<RawEntry>(children.Count);

        for (var i = 0; i < children.Count; i++)
        {
            var end = i + 1 < children.Count ? starts[i + 1] : closingTag;
            var segment = xml[starts[i]..end];
            var element = segment.TrimEnd();

            if (element.Length == 0 || !element.EndsWith('>'))
                return null;

            var key = KeyOf(element, keyAttribute);

            if (key is null || key != $"{children[i].Name.LocalName}/{AttributeValue(children[i], keyAttribute)}")
                return null;

            entries.Add(new RawEntry(key, element, segment[element.Length..]));
        }

        return new RawDocument(xml[..starts[0]], entries, xml[closingTag..]);
    }

    private static string? KeyOf(string elementXml, string keyAttribute)
    {
        XElement element;

        try
        {
            element = XElement.Parse(elementXml, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }

        var value = AttributeValue(element, keyAttribute);

        return string.IsNullOrWhiteSpace(value) ? null : $"{element.Name.LocalName}/{value}";
    }

    private static int? OffsetOf(XElement element, IReadOnlyList<int> lineStarts, string xml)
    {
        if (element is not IXmlLineInfo info || !info.HasLineInfo())
            return null;

        var line = info.LineNumber - 1;

        if (line < 0 || line >= lineStarts.Count)
            return null;

        // LinePosition points at the element name, so the opening angle bracket is one character back.
        var offset = lineStarts[line] + info.LinePosition - 2;

        return offset >= 0 && offset < xml.Length && xml[offset] == '<' ? offset : null;
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

    // XDocument.ToString drops the declaration and XDocument.Save through a StringWriter rewrites its
    // encoding as utf-16, so the header is carried across by hand.
    private static string Serialize(XDocument document, string originalXml)
    {
        var body = document.ToString(SaveOptions.DisableFormatting);

        if (document.Declaration is null)
            return body;

        var newLine = originalXml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        return $"{document.Declaration}{newLine}{body}";
    }

    private static XDocument? Parse(string xml, LoadOptions options = LoadOptions.PreserveWhitespace)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        try
        {
            return XDocument.Parse(xml, options);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string? ResolveKeyAttribute(XElement currentRoot, XElement incomingRoot)
    {
        foreach (var candidate in KeyAttributeCandidates)
        {
            if (Index(currentRoot, candidate) is not null && Index(incomingRoot, candidate) is not null)
                return candidate;
        }

        return null;
    }

    // A document whose entries are not all identified, or where two entries answer to the same name,
    // cannot be merged by name at all: there would be no way to say which entry an incoming one means.
    private static Dictionary<string, XElement>? Index(XElement root, string keyAttribute)
    {
        var entries = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var any = false;

        foreach (var element in root.Elements())
        {
            any = true;
            var value = AttributeValue(element, keyAttribute);

            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (!entries.TryAdd($"{element.Name.LocalName}/{value}", element))
                return null;
        }

        return any ? entries : null;
    }

    private static IReadOnlyList<XmlAttributeChange> DiffAttributes(XElement current, XElement incoming)
    {
        var changes = new List<XmlAttributeChange>();

        foreach (var attribute in incoming.Attributes())
        {
            var currentValue = current.Attribute(attribute.Name)?.Value;

            if (currentValue != attribute.Value)
                changes.Add(new XmlAttributeChange(attribute.Name.LocalName, currentValue ?? string.Empty, attribute.Value));
        }

        foreach (var attribute in current.Attributes().Where(a => incoming.Attribute(a.Name) is null))
            changes.Add(new XmlAttributeChange(attribute.Name.LocalName, attribute.Value, string.Empty));

        return changes;
    }

    private static string ContentOf(XElement element) =>
        string.Concat(element.Elements().Select(child => child.ToString(SaveOptions.DisableFormatting)));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
}
