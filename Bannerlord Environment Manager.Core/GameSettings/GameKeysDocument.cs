using System.Text;
using System.Xml;

namespace BannerlordEnvironmentManager.Core.GameSettings;

// BannerlordGameKeys.xml, addressed as "<category>/<id>" so that a binding is identified by the
// category it lives in as well as its own Id: two categories can declare the same Id and they are
// not the same binding. The value is the inner XML of that entry's Keys element, which is what lets
// the three entry shapes the real files carry travel through one document without this file knowing
// any of them: a GameKey (KeyboardKey plus ControllerKey), a GameAxisKey (PositiveKey, NegativeKey,
// AxisKey) and a HotKey (one or more Key elements). All three are the game's own and the shape says
// nothing about who owns the binding: in the resting 1.4.8 install HotKey carries 128 of the 258
// bindings in the 24 stock categories, while five of the six mod-owned categories there use GameKey
// and only TroopSortHotkeyCategory uses HotKey. What keeps a mod's bindings out of an instance is
// never creating a category, not which element shape the binding takes.
//
// The whole file is written by the game as a single line with no whitespace, and its root carries a
// version attribute that belongs to the build that wrote it. Only the span inside a Keys element is
// ever rewritten, so the root, its attributes, the leading comment and every element BEM has not
// heard of come back exactly as they went in.
public sealed class GameKeysDocument : IGameSettingsDocument
{
    private readonly ConfigText source;
    private readonly List<Entry> entries;

    private GameKeysDocument(ConfigText source, List<Entry> entries)
    {
        this.source = source;
        this.entries = entries;
    }

    public static string KeyFor(string category, string id) => $"{category}/{id}";

    public static GameKeysDocument Parse(ConfigText source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new GameKeysDocument(source, ReadEntries(source.Value));
    }

    public IReadOnlyList<string> Categories =>
        [.. entries.Select(entry => entry.Category).Distinct(StringComparer.Ordinal)];

    public IReadOnlyDictionary<string, string> Values
    {
        get
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var entry in entries)
                values[entry.Key] = entry.Current;

            return values;
        }
    }

    public int Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var changed = 0;

        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Key, key, StringComparison.Ordinal) || entry.Current == value)
                continue;

            entry.Current = value;
            changed++;
        }

        return changed;
    }

    public string Text
    {
        get
        {
            var edited = entries
                .Where(entry => entry.Current != entry.Original)
                .OrderBy(entry => entry.Start)
                .ToList();

            if (edited.Count == 0)
                return source.Value;

            var builder = new StringBuilder(source.Value.Length);
            var written = 0;

            foreach (var entry in edited)
            {
                builder.Append(source.Value, written, entry.Start - written);
                builder.Append(entry.Render());
                written = entry.End;
            }

            builder.Append(source.Value, written, source.Value.Length - written);

            return builder.ToString();
        }
    }

    public byte[] ToBytes() => source.ToBytes(Text);

    private static List<Entry> ReadEntries(string xml)
    {
        var lineStarts = LineStarts(xml);
        var entries = new List<Entry>();

        using var reader = XmlReader.Create(
            new StringReader(xml),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = false });

        var lineInfo = (IXmlLineInfo)reader;
        string? category = null;
        string? id = null;
        var inEntry = false;
        var capturingId = false;
        var selfClosing = false;
        int? keysStart = null;
        int? keysEnd = null;

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    var name = reader.Name;
                    var isEmpty = reader.IsEmptyElement;

                    switch (name)
                    {
                        case "HotKeyCategory":
                            category = reader.GetAttribute("name");
                            break;
                        case "GameKey" or "GameAxisKey" or "HotKey":
                            inEntry = true;
                            id = null;
                            selfClosing = false;
                            keysStart = null;
                            keysEnd = null;
                            break;
                        case "Id" when inEntry:
                            id = isEmpty ? string.Empty : null;
                            capturingId = !isEmpty;
                            break;
                        case "Keys" when inEntry:
                            var nameStart = Offset(xml, lineStarts, lineInfo, name);
                            var afterStartTag = AfterStartTag(xml, nameStart, name);

                            // A self-closing <Keys/> holds no binding at all, so the span that would
                            // be rewritten is the whole tag and the entry renders itself back into a
                            // real element the moment it is given one.
                            selfClosing = isEmpty;
                            keysStart = isEmpty ? nameStart - 1 : afterStartTag;
                            keysEnd = isEmpty ? afterStartTag : null;
                            break;
                    }

                    break;

                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace when capturingId:
                    id = reader.Value;
                    break;

                case XmlNodeType.EndElement:
                    switch (reader.Name)
                    {
                        case "Id":
                            capturingId = false;
                            break;
                        case "Keys" when inEntry && keysStart is not null:
                            keysEnd = EndTagStart(xml, lineStarts, lineInfo, "Keys");
                            break;
                        case "GameKey" or "GameAxisKey" or "HotKey":
                            if (category is not null && id is not null && keysStart is { } start && keysEnd is { } end)
                                entries.Add(new Entry(
                                    category, id, start, end, selfClosing ? string.Empty : xml[start..end], selfClosing));

                            inEntry = false;
                            break;
                        case "HotKeyCategory":
                            category = null;
                            break;
                    }

                    break;
            }
        }

        return entries;
    }

    // The parser reports an element at the first character of its name, so the start tag runs from
    // there to the next unquoted '>'. Failing to find an element where the parser says it is means
    // the edit cannot be made without guessing at the layout, and a silent skip would report a
    // binding as already matching.
    private static int AfterStartTag(string xml, int index, string name)
    {
        var quote = '\0';

        for (var i = index; i < xml.Length; i++)
        {
            if (quote != '\0')
            {
                if (xml[i] == quote)
                    quote = '\0';
            }
            else if (xml[i] == '"' || xml[i] == '\'')
            {
                quote = xml[i];
            }
            else if (xml[i] == '>')
            {
                return i + 1;
            }
        }

        throw new InvalidDataException($"The '{name}' element's start tag is never closed.");
    }

    private static int EndTagStart(string xml, IReadOnlyList<int> lineStarts, IXmlLineInfo lineInfo, string name)
    {
        var index = Offset(xml, lineStarts, lineInfo, name) - 2;

        if (index < 0 || !xml.AsSpan(index).StartsWith($"</{name}", StringComparison.Ordinal))
            throw new InvalidDataException($"The '{name}' element's end tag is not where the parser reports it.");

        return index;
    }

    private static int Offset(string xml, IReadOnlyList<int> lineStarts, IXmlLineInfo lineInfo, string name)
    {
        if (!lineInfo.HasLineInfo())
            throw new InvalidDataException("The XML reader cannot report positions, so no element can be located.");

        var line = lineInfo.LineNumber - 1;

        if (line < 0 || line >= lineStarts.Count)
            throw new InvalidDataException($"The '{name}' element is reported on a line the file does not have.");

        var index = lineStarts[line] + lineInfo.LinePosition - 1;

        if (index < 0 || index > xml.Length)
            throw new InvalidDataException($"The '{name}' element is reported outside the file.");

        return index;
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

    private sealed class Entry(string category, string id, int start, int end, string original, bool selfClosing)
    {
        public string Category { get; } = category;

        public string Key { get; } = KeyFor(category, id);

        public int Start { get; } = start;

        public int End { get; } = end;

        public string Original { get; } = original;

        public string Current { get; set; } = original;

        // The span of a self-closing <Keys/> is the whole tag, so giving it a binding has to write the
        // element back out rather than drop the keys after a tag that already closed itself.
        public string Render() => selfClosing ? $"<Keys>{Current}</Keys>" : Current;
    }
}
