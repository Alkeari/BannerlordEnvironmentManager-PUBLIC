using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BannerlordEnvironmentManager.Core.Settings;

// The values of one MCM settings file, keyed by property name, and the edit that writes changed
// values back. A write touches only the edited values: an XML file keeps its declaration, layout
// and every element it does not own, and a JSON file keeps its whitespace, because only the byte
// span of each edited value is replaced.
public static class McmSettingsFile
{
    public static bool IsJson(string path) =>
        Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".json5", StringComparison.OrdinalIgnoreCase);

    public static bool IsXml(string path) =>
        Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> Read(string path, IEnumerable<string> propertyNames)
    {
        var names = new HashSet<string>(propertyNames, StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        if (IsXml(path))
        {
            var document = XDocument.Load(path);
            foreach (var element in document.Descendants().Where(e => !e.HasElements && names.Contains(e.Name.LocalName)))
                values.TryAdd(element.Name.LocalName, element.Value);
        }
        else if (IsJson(path))
        {
            var bytes = File.ReadAllBytes(path);
            foreach (var span in JsonValues(bytes, names))
                values.TryAdd(span.Name, span.Text);
        }

        return values;
    }

    public static void Write(string path, IReadOnlyDictionary<string, string> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
            return;

        if (IsXml(path))
            File.WriteAllBytes(path, EditXml(File.ReadAllBytes(path), edits));
        else if (IsJson(path))
            File.WriteAllBytes(path, EditJson(File.ReadAllBytes(path), edits));
        else
            throw new NotSupportedException(path);
    }

    // The whole new content is built before the file is opened for writing.
    public static byte[] EditXml(byte[] original, IReadOnlyDictionary<string, string> edits)
    {
        // Text surgery, not a re-serialize: only the text between an edited element's tags changes,
        // so the declaration, line endings, comments, BOM and other elements stay byte for byte.
        var bom = HasBom(original);
        var text = Encoding.UTF8.GetString(original, bom ? 3 : 0, original.Length - (bom ? 3 : 0));

        foreach (var (name, value) in edits)
        {
            var pattern = new Regex("(<" + Regex.Escape(name) + @"(?:\s[^>]*)?>)([^<]*)(</" + Regex.Escape(name) + ">)");
            text = pattern.Replace(text, match =>
                match.Groups[1].Value
                + System.Security.SecurityElement.Escape(MatchCase(WebUtility.HtmlDecode(match.Groups[2].Value), value))
                + match.Groups[3].Value, 1);
        }

        var body = Encoding.UTF8.GetBytes(text);
        return bom ? [.. Encoding.UTF8.GetPreamble(), .. body] : body;
    }

    public static byte[] EditJson(byte[] original, IReadOnlyDictionary<string, string> edits)
    {
        var spans = JsonValues(original, edits.Keys.ToHashSet(StringComparer.Ordinal))
            .GroupBy(span => span.Name).Select(group => group.First())
            .OrderByDescending(span => span.Start).ToList();

        var result = new List<byte>(original);
        foreach (var span in spans)
        {
            var replacement = Encoding.UTF8.GetBytes(JsonLiteral(span.Kind, edits[span.Name]));
            result.RemoveRange(span.Start, span.Length);
            result.InsertRange(span.Start, replacement);
        }
        return [.. result];
    }

    // XML booleans written by MCM are "True"/"False"; keep whichever casing the file already used.
    private static string MatchCase(string existing, string value) =>
        bool.TryParse(value, out var flag) && bool.TryParse(existing, out _)
            ? (existing.Length > 0 && char.IsUpper(existing[0])
                ? (flag ? "True" : "False")
                : (flag ? "true" : "false"))
            : value;

    private static bool HasBom(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private sealed record JsonSpan(string Name, string Text, int Start, int Length, JsonTokenType Kind);

    private static List<JsonSpan> JsonValues(byte[] bytes, ISet<string> names)
    {
        var found = new List<JsonSpan>();
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        string? pending = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                pending = reader.GetString();
                continue;
            }

            if (pending is not null && names.Contains(pending)
                && reader.TokenType is JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False)
            {
                var start = (int)reader.TokenStartIndex;
                var length = (int)reader.BytesConsumed - start;
                var text = reader.TokenType switch
                {
                    JsonTokenType.String => reader.GetString() ?? string.Empty,
                    JsonTokenType.True => "true",
                    JsonTokenType.False => "false",
                    _ => Encoding.UTF8.GetString(bytes, start, length),
                };
                found.Add(new JsonSpan(pending, text, start, length, reader.TokenType));
            }

            pending = null;
        }
        return found;
    }

    private static string JsonLiteral(JsonTokenType kind, string value) => kind switch
    {
        JsonTokenType.String => JsonSerializer.Serialize(value),
        JsonTokenType.True or JsonTokenType.False =>
            bool.TryParse(value, out var flag) ? (flag ? "true" : "false") : JsonSerializer.Serialize(value),
        _ => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("R", CultureInfo.InvariantCulture)
            : JsonSerializer.Serialize(value),
    };
}
