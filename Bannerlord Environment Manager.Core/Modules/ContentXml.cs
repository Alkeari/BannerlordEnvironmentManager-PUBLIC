using System.Xml;
using System.Xml.Linq;
using BannerlordEnvironmentManager.Core.Io;
using XmlText = BannerlordEnvironmentManager.Core.Io.XmlText;

namespace BannerlordEnvironmentManager.Core.Modules;

// Where a content file stops parsing. Line and Column are where the parser gave up; Unclosed is the
// element still open at that moment, which for a mismatched end tag is the one the author forgot to
// close. A parse failure proves malformed XML only: a file that parses can still hold game data
// Bannerlord rejects, and nothing here claims otherwise.
public sealed record ContentXmlProblem(
    string FilePath,
    int Line,
    int Column,
    string Message,
    string? Unclosed,
    int UnclosedLine,
    int UnclosedColumn);

// One edit a repair makes: the start tag at Line and Column gains the "/" that makes it empty.
public sealed record ContentXmlEdit(string Element, int Line, int Column, string Before, string After);

public sealed record ContentXmlRepair(
    ContentXmlProblem Problem,
    IReadOnlyList<ContentXmlEdit> Edits,
    XmlText Original,
    string RepairedText)
{
    public string FilePath => Problem.FilePath;
}

// Loaded says whether the game reads the file at all: true for SubModule.xml, a file its XmlNode list
// registers and a file under ModuleData\Languages or GUI, which the engine reads by folder; false for any other
// file, which nothing registers; null when SubModule.xml itself cannot be read, so nothing is known.
public sealed record ContentXmlFinding(ContentXmlProblem Problem, ContentXmlRepair? Repair, bool? Loaded = null);

public enum ContentXmlApplyResult
{
    Applied,
    FileChanged,
    StillBroken,
    Failed
}

// Bannerlord reads a module's content XML (characters, items, skins, races) as it loads, and one
// unclosed element stops the whole load with an error that names neither the file nor the module.
// A valid SubModule.xml says nothing about those files, so they are read here in their own right.
//
// A repair is offered only for the one shape whose intent the file itself proves: a start tag
// followed by nothing but whitespace and then its parent's end tag, as in an unclosed <skin ...>
// before </race>. That element was meant to be empty, so the repair adds the "/" its own siblings
// carry. Anything else (content inside the open element, an end tag naming some other element, text
// that still fails after every such edit) stays a diagnostic with no edit, since guessing at the
// author's structure could change what the mod defines.
public static class ContentXml
{
    private const int MaxEdits = 200;

    private static readonly XmlReaderSettings Settings = new()
    {
        // Ignore, never Parse: a DOCTYPE is skipped rather than refused, and no entity or external
        // resource is ever resolved from a file a stranger wrote.
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        CloseInput = true
    };

    public static ContentXmlProblem? Check(string text, string filePath = "")
    {
        ArgumentNullException.ThrowIfNull(text);

        var open = new Stack<(string Name, int Line, int Column)>();

        try
        {
            using var reader = XmlReader.Create(new StringReader(text), Settings);
            var info = (IXmlLineInfo)reader;

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && !reader.IsEmptyElement)
                    open.Push((reader.Name, info.LineNumber, info.LinePosition));
                else if (reader.NodeType == XmlNodeType.EndElement && open.Count > 0)
                    open.Pop();
            }

            return null;
        }
        catch (XmlException ex)
        {
            var (name, line, column) = open.Count > 0 ? open.Peek() : (null!, 0, 0);
            return new ContentXmlProblem(filePath, ex.LineNumber, ex.LinePosition, ex.Message, name, line, column);
        }
    }

    public static ContentXmlFinding? Inspect(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var original = XmlTextFile.Read(filePath);
        var problem = Check(original.Value, filePath);

        return problem is null ? null : new ContentXmlFinding(problem, ProposeRepair(original, problem));
    }

    // Every .xml under the module folder, SubModule.xml included, in a stable order. A file that cannot
    // be read is reported as a problem of its own rather than dropped.
    public static IReadOnlyList<ContentXmlFinding> ScanModule(string moduleFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleFolder);

        if (!Directory.Exists(moduleFolder))
            return [];

        var findings = new List<ContentXmlFinding>();
        var loaded = LoadedFiles(moduleFolder);
        var files = Directory.EnumerateFiles(moduleFolder, "*.xml", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive
        }).Order(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            try
            {
                if (Inspect(file) is { } finding)
                    findings.Add(finding with { Loaded = IsLoaded(moduleFolder, file, loaded) });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                findings.Add(new ContentXmlFinding(new ContentXmlProblem(file, 0, 0, ex.Message, null, 0, 0), null,
                    IsLoaded(moduleFolder, file, loaded)));
            }
        }

        return findings;
    }

    // The files SubModule.xml registers through its XmlNode list, resolved the way the engine resolves
    // them: ModuleData\<path>.xml when that file exists, else every .xml directly inside a
    // ModuleData\<path> folder. Null when SubModule.xml is missing or does not parse.
    public static IReadOnlySet<string>? LoadedFiles(string moduleFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleFolder);

        var manifest = Path.Combine(moduleFolder, "SubModule.xml");
        XDocument document;

        try
        {
            using var reader = XmlReader.Create(manifest, Settings);
            document = XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in document.Descendants("XmlNode").Select(node => node.Element("XmlName")))
        {
            var path = name?.Attribute("path")?.Value;

            if (string.IsNullOrWhiteSpace(path))
                continue;

            string target;

            try
            {
                target = Path.GetFullPath(Path.Combine(moduleFolder, "ModuleData",
                    path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(target + ".xml"))
                files.Add(target + ".xml");
            else if (Directory.Exists(target))
                files.UnionWith(Directory.GetFiles(target, "*.xml", SearchOption.TopDirectoryOnly).Select(Path.GetFullPath));
        }

        return files;
    }

    private static bool? IsLoaded(string moduleFolder, string file, IReadOnlySet<string>? loaded)
    {
        var full = Path.GetFullPath(file);

        if (Path.GetFileName(full).Equals("SubModule.xml", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(full)!.Equals(Path.GetFullPath(moduleFolder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return true;

        // Language files and Gauntlet's prefabs and brushes are read by their folder, never registered.
        var root = Path.GetFullPath(moduleFolder);
        string[] byFolder = [Path.Combine(root, "ModuleData", "Languages"), Path.Combine(root, "GUI")];

        if (byFolder.Any(folder => full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            return true;

        return loaded?.Contains(full);
    }

    public static ContentXmlRepair? ProposeRepair(XmlText original, ContentXmlProblem problem)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(problem);

        var text = original.Value;
        var edits = new List<ContentXmlEdit>();
        var current = problem;

        while (current is not null)
        {
            if (edits.Count >= MaxEdits || !TryCloseEmpty(text, current, out var repaired, out var edit))
                return null;

            text = repaired;
            edits.Add(edit);
            current = Check(text, problem.FilePath);
        }

        return edits.Count == 0 ? null : new ContentXmlRepair(problem, edits, original, text);
    }

    // Writes the repair only when the file still holds exactly the bytes the preview was made from and
    // the repaired text parses. The first write keeps the file as it was beside it (.bem.bak), so
    // Undo puts the original back byte for byte.
    public static ContentXmlApplyResult Apply(ContentXmlRepair repair)
    {
        ArgumentNullException.ThrowIfNull(repair);

        try
        {
            var now = XmlTextFile.Read(repair.FilePath);

            if (!string.Equals(now.Value, repair.Original.Value, StringComparison.Ordinal)
                || now.HasByteOrderMark != repair.Original.HasByteOrderMark)
                return ContentXmlApplyResult.FileChanged;

            if (Check(repair.RepairedText) is not null)
                return ContentXmlApplyResult.StillBroken;

            AtomicXmlFile.Save(repair.Original.ToBytes(repair.RepairedText), repair.FilePath, writeBackup: true);
            return ContentXmlApplyResult.Applied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ContentXmlApplyResult.Failed;
        }
    }

    // Content files under the module folder that carry the backup a repair left. SubModule.xml is left
    // out: its backup belongs to the validator's own version rewrites and is restored by those.
    public static IReadOnlyList<string> RepairedFiles(string moduleFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleFolder);

        if (!Directory.Exists(moduleFolder))
            return [];

        return [.. Directory.EnumerateFiles(moduleFolder, "*.xml" + AtomicXmlFile.BackupExtension, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive
            })
            .Select(backup => backup[..^AtomicXmlFile.BackupExtension.Length])
            .Where(file => !Path.GetFileName(file).Equals("SubModule.xml", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    public static bool CanUndo(string filePath) => AtomicXmlFile.HasBackup(filePath);

    public static bool Undo(string filePath) => AtomicXmlFile.Restore(filePath);

    private static bool TryCloseEmpty(string text, ContentXmlProblem problem, out string repaired, out ContentXmlEdit edit)
    {
        repaired = text;
        edit = null!;

        if (problem.Unclosed is null || problem.UnclosedLine <= 0)
            return false;

        var lineStarts = LineStarts(text);

        if (problem.UnclosedLine > lineStarts.Count)
            return false;

        // The reader reports an element at its name, one past the "<".
        var nameStart = lineStarts[problem.UnclosedLine - 1] + problem.UnclosedColumn - 1;

        if (nameStart < 1 || nameStart > text.Length || text[nameStart - 1] != '<'
            || string.CompareOrdinal(text, nameStart, problem.Unclosed, 0, problem.Unclosed.Length) != 0)
            return false;

        var close = EndOfStartTag(text, nameStart + problem.Unclosed.Length);

        if (close < 0 || text[close - 1] == '/')
            return false;

        var next = close + 1;
        while (next < text.Length && char.IsWhiteSpace(text[next]))
            next++;

        // Only an end tag may follow, and it must close the element enclosing this one: that is the
        // mismatch an unclosed empty element produces, and nothing else is.
        if (next + 1 >= text.Length || text[next] != '<' || text[next + 1] != '/')
            return false;

        var endName = ReadName(text, next + 2);

        if (endName.Length == 0 || endName == problem.Unclosed || !EnclosingIs(text, nameStart - 1, endName))
            return false;

        var lineStart = lineStarts[problem.UnclosedLine - 1];
        var lineEnd = LineEnd(text, lineStart);
        var before = text[lineStart..lineEnd].Trim();

        repaired = string.Concat(text.AsSpan(0, close), "/", text.AsSpan(close));
        edit = new ContentXmlEdit(problem.Unclosed, problem.UnclosedLine, problem.UnclosedColumn - 1, before,
            repaired[lineStart..LineEnd(repaired, lineStart)].Trim());
        return true;
    }

    // The element enclosing the tag at tagStart, read from the text itself: the nearest earlier start
    // tag not already closed before it.
    private static bool EnclosingIs(string text, int tagStart, string name)
    {
        var problem = Check(text[..tagStart] + "<x/>");

        return problem is { Unclosed: { } open } && open == name;
    }

    private static int EndOfStartTag(string text, int from)
    {
        char? quote = null;

        for (var i = from; i < text.Length; i++)
        {
            var c = text[i];

            if (quote is { } q)
            {
                if (c == q)
                    quote = null;
            }
            else if (c is '"' or '\'')
                quote = c;
            else if (c == '>')
                return i;
            else if (c == '<')
                return -1;
        }

        return -1;
    }

    private static string ReadName(string text, int from)
    {
        var end = from;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '-' or '.' or ':'))
            end++;

        return text[from..end];
    }

    // Line starts the way XmlReader counts lines: \r\n, \r and \n each end one.
    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;

                starts.Add(i + 1);
            }
            else if (text[i] == '\n')
                starts.Add(i + 1);
        }

        return starts;
    }

    private static int LineEnd(string text, int lineStart)
    {
        var end = text.IndexOfAny(['\r', '\n'], lineStart);
        return end < 0 ? text.Length : end;
    }
}
