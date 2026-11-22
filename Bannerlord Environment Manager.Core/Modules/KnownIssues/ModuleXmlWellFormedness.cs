using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using BannerlordEnvironmentManager.Core.Io;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

// One edit of a proposed repair: insert "/" before the ">" that ends the start tag of an element the file
// meant to be empty, or with ClosesStartTag the ">" a start tag never got. Line and column are 1-based.
public sealed record XmlRepairEdit(int Line, int Column, string Element, bool ClosesStartTag = false)
{
    public string Describe() => Strings.Current.Format(
        ClosesStartTag ? "Core.Modules.XmlRepair.EditCloseTag" : "Core.Modules.XmlRepair.Edit", Line, Column, Element);
}

public sealed record XmlRepairResult(string FilePath, string BackupPath);

// A repair BEM can propose, never applies on its own: the exact edits, and the repaired text that parsed.
public sealed record XmlRepair(string FilePath, IReadOnlyList<XmlRepairEdit> Edits, string RepairedText, Encoding Encoding, bool HadByteOrderMark)
{
    public string Describe() => string.Join("; ", Edits.Select(e => e.Describe()));

    // The original bytes are copied to the backup folder first, the repaired text is parsed once more,
    // and only then is the file replaced, in its own encoding and byte order mark. Nothing else in the
    // file changes. Undo puts the original bytes back.
    public XmlRepairResult Apply(string backupFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFolder);

        if (ModuleXmlWellFormedness.FirstError(RepairedText) is not null)
            throw new InvalidOperationException(Strings.Current.Format("Core.Modules.XmlRepair.DoesNotParse", FilePath));

        Directory.CreateDirectory(backupFolder);

        var backup = Path.Combine(
            backupFolder,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Path.GetFileName(FilePath)}.original");

        File.Copy(FilePath, backup, overwrite: false);

        var preamble = HadByteOrderMark ? Encoding.GetPreamble() : [];
        var body = Encoding.GetBytes(RepairedText);
        var pending = FilePath + ".bem-repair";

        File.WriteAllBytes(pending, [.. preamble, .. body]);
        File.Move(pending, FilePath, overwrite: true);

        return new XmlRepairResult(FilePath, backup);
    }

    public static void Undo(XmlRepairResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        LinkSafeFile.Copy(result.BackupPath, result.FilePath);
    }
}

public sealed record XmlParseError(int Line, int Column, string Message);

// A content file of an enabled module that is not well-formed XML. Syntax only: a file that parses is not
// thereby content Bannerlord accepts.
public sealed record MalformedModuleXml(
    ModuleId ModuleId,
    string ModuleName,
    string FilePath,
    string RelativePath,
    int Line,
    int Column,
    string Message,
    // The module's SubModule.xml names the file in an XmlName path, which is how the engine finds the
    // content it merges (skins, items, troops). An unnamed file may never be read.
    bool IsReferenced,
    // Present only when the shape has exactly one safe repair; ambiguous cases stay diagnostics.
    XmlRepair? Repair);

// Gap G2 in the findings catalog: an unclosed <skin> inside <race> in the BetterFemaleBody mods' skin XML
// ("The 'skin' start tag on line 11 position 10 does not match the end tag of 'race'") crashed 11 of 11
// launches at 46 to 161 seconds, and nothing before the launch looked at content XML. The strict parse of
// 2,748 files took seconds, and results are cached per file by size and write time.
public static class ModuleXmlWellFormedness
{
    private const int MaxRepairSteps = 2048;

    private static readonly Regex StartTagWithoutEnd = new(
        """^(?<tag><(?<name>[\w:.\-]+)(?:\s+[\w:.\-]+\s*=\s*(?:"[^"]*"|'[^']*'))*)\s*\z""",
        RegexOptions.CultureInvariant);

    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Written), XmlParseError?> Cache = new();

    public static IReadOnlyList<MalformedModuleXml> Inspect(IReadOnlyList<ModuleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var found = new List<MalformedModuleXml>();

        foreach (var entry in entries)
        {
            if (!entry.IsEnabled || entry.IsOfficial || entry.Manifest is not { } manifest || string.IsNullOrWhiteSpace(manifest.FolderPath))
                continue;

            var data = Path.Combine(manifest.FolderPath, "ModuleData");

            if (!Directory.Exists(data))
                continue;

            IReadOnlyList<string> files;

            try
            {
                files = [.. Directory.EnumerateFiles(data, "*.xml", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            HashSet<string>? referenced = null;

            foreach (var file in files)
            {
                if (CachedError(file) is not { } error)
                    continue;

                referenced ??= ReferencedPaths(manifest.ManifestPath);

                var relative = Path.GetRelativePath(manifest.FolderPath, file);

                found.Add(new MalformedModuleXml(
                    entry.Id,
                    entry.DisplayName,
                    file,
                    relative,
                    error.Line,
                    error.Column,
                    error.Message,
                    IsReferenced(referenced, data, file),
                    ProposeRepair(file)));
            }
        }

        return found;
    }

    // The first well-formedness error in the text, or null when it parses. External entities and DTDs are
    // never resolved: a mod's file must not make BEM fetch or read anything.
    public static XmlParseError? FirstError(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Walk(text).Error;
    }

    // The proven shape only: an element closed with ">" where "/>" was meant, so the parent's end tag
    // arrives while that element is still open, and nothing but whitespace or comments sits between them.
    // Each step must be that shape and the final text must parse, or there is no repair. An element with
    // children or text before the mismatch is ambiguous (a missing end tag, or a misplaced one) and gets
    // no edit.
    public static XmlRepair? ProposeRepair(string path)
    {
        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var (encoding, hadBom, text) = Decode(bytes);
        var repaired = ProposeRepairText(text, out var edits);

        return repaired is null ? null : new XmlRepair(path, edits, repaired, encoding, hadBom);
    }

    public static string? ProposeRepairText(string text, out IReadOnlyList<XmlRepairEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(text);

        var made = new List<XmlRepairEdit>();
        edits = made;

        var current = text;

        for (var step = 0; step < MaxRepairSteps; step++)
        {
            var walk = Walk(current);

            if (walk.Error is null)
                return made.Count == 0 ? null : current;

            if (UnfinishedStartTag(current, walk.Error) is { } unfinished)
            {
                var (tagLine, tagColumn) = LineAndColumn(current, unfinished.At);
                made.Add(new XmlRepairEdit(tagLine, tagColumn, unfinished.Element, ClosesStartTag: true));
                current = current.Insert(unfinished.At, ">");
                continue;
            }

            if (walk.Open.Count < 2)
                return null;

            var unclosed = walk.Open[^1];
            var parent = walk.Open[^2];

            if (unclosed.HadContent || !EndTagAt(current, walk.Error, parent.Name))
                return null;

            if (StartTagEnd(current, unclosed) is not { } close)
                return null;

            var (line, column) = LineAndColumn(current, close);
            made.Add(new XmlRepairEdit(line, column, unclosed.Name));
            current = current.Insert(close, "/");
        }

        return null;
    }

    // <hair_mesh name="a" <style_tags>: complete attributes, then the next tag where the ">" belonged. A "<"
    // can never sit inside a start tag, so the tag can only have been meant to end there.
    private static (int At, string Element)? UnfinishedStartTag(string text, XmlParseError error)
    {
        if (OffsetOf(text, error.Line, error.Column) is not { } at || at <= 0 || at >= text.Length || text[at] != '<')
            return null;

        var start = text.LastIndexOf('<', at - 1);

        if (start < 0)
            return null;

        var match = StartTagWithoutEnd.Match(text[start..at]);

        return match.Success ? (start + match.Groups["tag"].Length, match.Groups["name"].Value) : null;
    }

    private static XmlParseError? CachedError(string file)
    {
        FileInfo info;

        try
        {
            info = new FileInfo(file);

            if (!info.Exists)
                return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return Cache.GetOrAdd((info.FullName, info.Length, info.LastWriteTimeUtc), _ =>
        {
            try
            {
                return FirstError(Decode(File.ReadAllBytes(file)).Text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });
    }

    private sealed class OpenElement(string name, int line, int column)
    {
        public string Name { get; } = name;

        public int Line { get; } = line;

        public int Column { get; } = column;

        public bool HadContent { get; set; }
    }

    private sealed record WalkResult(XmlParseError? Error, IReadOnlyList<OpenElement> Open);

    private static WalkResult Walk(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = false,
            IgnoreWhitespace = false,
            IgnoreProcessingInstructions = false,
            CheckCharacters = true
        };

        var open = new List<OpenElement>();

        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var info = (IXmlLineInfo)reader;

            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (open.Count > 0)
                            open[^1].HadContent = true;

                        if (!reader.IsEmptyElement)
                            open.Add(new OpenElement(reader.Name, info.LineNumber, info.LinePosition));
                        break;

                    case XmlNodeType.EndElement:
                        if (open.Count > 0)
                            open.RemoveAt(open.Count - 1);
                        break;

                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.EntityReference:
                        if (open.Count > 0)
                            open[^1].HadContent = true;
                        break;
                }
            }

            return new WalkResult(null, open);
        }
        catch (XmlException ex)
        {
            return new WalkResult(new XmlParseError(ex.LineNumber, ex.LinePosition, ex.Message), open);
        }
    }

    // The mismatched end tag the parser stopped on is the parent's: "</race" at the reported position
    // (which names the tag's name, two characters after "<").
    private static bool EndTagAt(string text, XmlParseError error, string parentName)
    {
        if (OffsetOf(text, error.Line, error.Column) is not { } at)
            return false;

        foreach (var start in new[] { at - 2, at - 1, at })
        {
            if (start < 0 || start + 2 + parentName.Length > text.Length)
                continue;

            if (string.CompareOrdinal(text, start, "</" + parentName, 0, parentName.Length + 2) != 0)
                continue;

            var after = start + 2 + parentName.Length;

            if (after == text.Length || text[after] == '>' || char.IsWhiteSpace(text[after]))
                return true;
        }

        return false;
    }

    // The ">" ending the element's start tag, skipping quoted attribute values; null when the tag is
    // already self-closing or cannot be read.
    private static int? StartTagEnd(string text, OpenElement element)
    {
        if (OffsetOf(text, element.Line, element.Column) is not { } nameStart)
            return null;

        char? quote = null;

        for (var i = nameStart; i < text.Length; i++)
        {
            var c = text[i];

            if (quote is { } q)
            {
                if (c == q)
                    quote = null;
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c == '<')
                return null;

            if (c == '>')
                return i > 0 && text[i - 1] == '/' ? null : i;
        }

        return null;
    }

    // XmlReader counts lines across "\r\n", "\r" and "\n" alike and columns from 1.
    private static int? OffsetOf(string text, int line, int column)
    {
        if (line < 1 || column < 1)
            return null;

        var current = 1;
        var i = 0;

        while (current < line && i < text.Length)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                current++;
            }
            else if (text[i] == '\n')
            {
                current++;
            }

            i++;
        }

        var offset = i + column - 1;

        return current == line && offset <= text.Length ? offset : null;
    }

    private static (int Line, int Column) LineAndColumn(string text, int offset)
    {
        var line = 1;
        var lineStart = 0;

        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < offset && text[i + 1] == '\n')
                    i++;
                line++;
                lineStart = i + 1;
            }
            else if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, offset - lineStart + 1);
    }

    private static (Encoding Encoding, bool HadBom, string Text) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(true), true, new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3));

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (new UnicodeEncoding(false, true), true, Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (new UnicodeEncoding(true, true), true, Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2));

        return (new UTF8Encoding(false), false, new UTF8Encoding(false).GetString(bytes));
    }

    // Every XmlName path the manifest declares, as "skins" or "Folder/file" without an extension.
    private static HashSet<string> ReferencedPaths(string manifestPath)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(manifestPath, settings);

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element
                    && string.Equals(reader.Name, "XmlName", StringComparison.OrdinalIgnoreCase)
                    && reader.GetAttribute("path") is { Length: > 0 } path)
                {
                    paths.Add(Normalize(path));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException)
        {
            // An unreadable manifest names nothing; the load-order check already reports it.
        }

        return paths;
    }

    private static bool IsReferenced(HashSet<string> referenced, string moduleData, string file)
    {
        var relative = Path.GetRelativePath(moduleData, file);
        var withoutExtension = Path.ChangeExtension(relative, null);

        return referenced.Contains(Normalize(withoutExtension)) || referenced.Contains(Normalize(relative));
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}
