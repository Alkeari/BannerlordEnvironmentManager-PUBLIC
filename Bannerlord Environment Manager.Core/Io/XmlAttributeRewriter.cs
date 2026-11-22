using System.Text;
using System.Xml;

namespace BannerlordEnvironmentManager.Core.Io;

public sealed record XmlElementView(IReadOnlyList<string> Path, string Name, IReadOnlyDictionary<string, string> Attributes);

public sealed record XmlRewrite(string Xml, int Changed);

// A mod author's SubModule.xml is not BEM's file to reformat. Rewriting one through XLinq loses
// everything the object model does not represent: it prepends a byte order mark, rewrites every LF
// as CRLF, re-cases the encoding declaration, normalizes single quotes to double and respaces
// self-closing tags. Replacing the attribute value where it sits in the original text leaves every
// other byte, including comments and elements BEM has never heard of, exactly as the author wrote it.
public static class XmlAttributeRewriter
{
    public static XmlRewrite SetAttributes(string xml, Func<XmlElementView, (string Name, string Value)?> choose)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(choose);

        var lineStarts = LineStarts(xml);
        var edits = new List<(int Start, int End, string Value)>();
        var path = new List<string>();

        using var reader = XmlReader.Create(
            new StringReader(xml),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = false });

        var lineInfo = (IXmlLineInfo)reader;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                path.RemoveAt(path.Count - 1);
                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
                continue;

            var name = reader.Name;
            var isEmpty = reader.IsEmptyElement;
            var attributes = ReadAttributes(reader);

            if (choose(new XmlElementView(path, name, attributes)) is { } edit
                && attributes.TryGetValue(edit.Name, out var current)
                && !string.Equals(current, edit.Value, StringComparison.Ordinal))
            {
                edits.Add(ValueSpan(xml, lineStarts, reader, lineInfo, edit));
                reader.MoveToElement();
            }

            if (!isEmpty)
                path.Add(name);
        }

        if (edits.Count == 0)
            return new XmlRewrite(xml, 0);

        var builder = new StringBuilder(xml.Length);
        var written = 0;

        foreach (var (start, end, value) in edits)
        {
            builder.Append(xml, written, start - written);
            builder.Append(Escape(value));
            written = end;
        }

        builder.Append(xml, written, xml.Length - written);

        return new XmlRewrite(builder.ToString(), edits.Count);
    }

    private static Dictionary<string, string> ReadAttributes(XmlReader reader)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!reader.MoveToFirstAttribute())
            return attributes;

        do
        {
            attributes[reader.Name] = reader.Value;
        }
        while (reader.MoveToNextAttribute());

        reader.MoveToElement();
        return attributes;
    }

    // Failing to find the value where the parser says it is means the edit cannot be made without
    // guessing at the file's layout, and a silent skip would report the manifest as already correct.
    private static (int Start, int End, string Value) ValueSpan(
        string xml,
        IReadOnlyList<int> lineStarts,
        XmlReader reader,
        IXmlLineInfo lineInfo,
        (string Name, string Value) edit)
    {
        if (!lineInfo.HasLineInfo() || !reader.MoveToAttribute(edit.Name))
            throw new InvalidDataException($"The '{edit.Name}' attribute could not be located in the file.");

        var line = lineInfo.LineNumber - 1;

        if (line < 0 || line >= lineStarts.Count)
            throw new InvalidDataException($"The '{edit.Name}' attribute is reported on a line the file does not have.");

        var index = lineStarts[line] + lineInfo.LinePosition - 1;

        if (index < 0 || !xml.AsSpan(index).StartsWith(edit.Name, StringComparison.Ordinal))
            throw new InvalidDataException($"The '{edit.Name}' attribute is not where the parser reports it.");

        index = SkipWhitespace(xml, index + edit.Name.Length);

        if (index >= xml.Length || xml[index] != '=')
            throw new InvalidDataException($"The '{edit.Name}' attribute has no value to rewrite.");

        index = SkipWhitespace(xml, index + 1);

        if (index >= xml.Length || (xml[index] != '"' && xml[index] != '\''))
            throw new InvalidDataException($"The '{edit.Name}' attribute value is not quoted.");

        var start = index + 1;
        var end = xml.IndexOf(xml[index], start);

        if (end < 0)
            throw new InvalidDataException($"The '{edit.Name}' attribute value is never closed.");

        return (start, end, edit.Value);
    }

    private static int SkipWhitespace(string xml, int index)
    {
        while (index < xml.Length && char.IsWhiteSpace(xml[index]))
            index++;

        return index;
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);

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
