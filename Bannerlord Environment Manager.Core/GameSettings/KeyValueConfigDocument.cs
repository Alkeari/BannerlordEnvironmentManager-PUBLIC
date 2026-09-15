namespace BannerlordEnvironmentManager.Core.GameSettings;

// engine_config.txt ("key = value") and BannerlordConfig.txt ("Key=Value") in one line-oriented
// document, because the only thing that separates them is how much whitespace sits around the equals
// sign and that is preserved rather than parsed.
//
// Editing happens in place, line by line. Parsing to a dictionary and regenerating would look
// tidier and would be wrong: engine_config.txt declares antialiasing_technique twice, at lines 53
// and 85 of every real instance, and that is the game's own doing. A regenerating writer drops one
// occurrence and reorders the rest, which changes a file the game wrote and BEM does not own.
public sealed class KeyValueConfigDocument : IGameSettingsDocument
{
    private readonly ConfigText source;
    private readonly List<Line> lines;

    private KeyValueConfigDocument(ConfigText source, List<Line> lines)
    {
        this.source = source;
        this.lines = lines;
    }

    public static KeyValueConfigDocument Parse(ConfigText source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new KeyValueConfigDocument(source, [.. SplitLines(source.Value).Select(ParseLine)]);
    }

    // Last occurrence wins, which is what a parser reading the file top to bottom into one setting
    // ends up with. Both of engine_config.txt's antialiasing_technique lines carry the same value in
    // every instance seen so far, so this only decides a case the game itself has not produced.
    public IReadOnlyDictionary<string, string> Values
    {
        get
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var line in lines.Where(line => line.Key is not null))
                values[line.Key!] = line.Value;

            return values;
        }
    }

    public int Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var changed = 0;

        foreach (var line in lines)
        {
            if (!string.Equals(line.Key, key, StringComparison.Ordinal) || line.Value == value)
                continue;

            line.Value = value;
            changed++;
        }

        return changed;
    }

    public string Text => string.Concat(lines.Select(line => line.Prefix + line.Value + line.Suffix + line.Ending));

    public byte[] ToBytes() => source.ToBytes(Text);

    private static Line ParseLine((string Content, string Ending) raw)
    {
        var (content, ending) = raw;
        var separator = content.IndexOf('=', StringComparison.Ordinal);
        var trimmed = content.AsSpan().TrimStart();

        if (separator < 0 || trimmed.IsEmpty || trimmed[0] is '#' or ';')
            return new Line(content, null, string.Empty, string.Empty, ending);

        var key = content[..separator].Trim();

        if (key.Length == 0)
            return new Line(content, null, string.Empty, string.Empty, ending);

        var start = separator + 1;
        while (start < content.Length && char.IsWhiteSpace(content[start]))
            start++;

        var end = content.Length;
        while (end > start && char.IsWhiteSpace(content[end - 1]))
            end--;

        return new Line(content[..start], key, content[start..end], content[end..], ending);
    }

    // The terminator travels with the line it ends, so a file that ends without one (every real
    // BannerlordConfig.txt does) rebuilds without one, and a file mixing CRLF and LF keeps each line
    // ending exactly where it was.
    private static List<(string Content, string Ending)> SplitLines(string text)
    {
        var lines = new List<(string, string)>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                var ending = i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : "\r";
                lines.Add((text[start..i], ending));
                i += ending.Length - 1;
                start = i + 1;
            }
            else if (text[i] == '\n')
            {
                lines.Add((text[start..i], "\n"));
                start = i + 1;
            }
        }

        if (start < text.Length)
            lines.Add((text[start..], string.Empty));

        return lines;
    }

    private sealed class Line(string prefix, string? key, string value, string suffix, string ending)
    {
        public string Prefix { get; } = prefix;

        public string? Key { get; } = key;

        public string Value { get; set; } = value;

        public string Suffix { get; } = suffix;

        public string Ending { get; } = ending;
    }
}
