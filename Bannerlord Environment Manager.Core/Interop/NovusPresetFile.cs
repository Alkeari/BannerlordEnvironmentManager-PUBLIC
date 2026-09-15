using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Interop;

// The preset format of the Novus launcher, which BUTR also reads and writes. Two writers exist in the
// wild and they disagree on bytes: BUTR emits a declaration, a UTF-8 BOM and CRLF, ModdingTools.com
// emits none of the three. Both are read by both, so the reader must not require any of them.
public static class NovusPresetFile
{
    public const string DefaultName = "Bannerlord Environment Manager Load Order";

    public const string DefaultCreatedBy = "Bannerlord Environment Manager";

    public static string Write(
        IReadOnlyList<LoadOrderFileEntry> entries,
        string name = DefaultName,
        string createdBy = DefaultCreatedBy,
        DateOnly? lastUpdated = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var root = new XElement("Preset",
            new XAttribute("Name", name),
            new XAttribute("CreatedBy", createdBy),
            // The pattern is day first, and "/" in a .NET date pattern is the culture's separator
            // placeholder rather than a literal, so this is formatted invariantly or a German machine
            // would write 11.08.2026.
            new XAttribute("LastUpdated", (lastUpdated ?? DateOnly.FromDateTime(DateTime.Now))
                .ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)));

        foreach (var entry in entries)
        {
            var id = entry.Id?.Trim() ?? string.Empty;

            if (id.Length == 0)
                continue;

            // RequiredVersion is dereferenced without a null check by BUTR's reader, so a missing
            // attribute throws and cancels the entire import. Empty is fine; absent is fatal.
            root.Add(new XElement("PresetModule",
                new XAttribute("Id", id),
                new XAttribute("RequiredVersion", entry.Version?.Trim() ?? string.Empty),
                new XAttribute("URL", entry.Url ?? string.Empty)));
        }

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Replace,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        var text = new Utf8StringWriter();

        using (var writer = XmlWriter.Create(text, settings))
            new XDocument(root).Save(writer);

        return text.ToString();
    }

    public static LoadOrderFileRead Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        XDocument document;

        try
        {
            document = XDocument.Parse(text.TrimStart('\uFEFF'));
        }
        catch (XmlException ex)
        {
            return LoadOrderFileRead.Unreadable(Strings.Current.Format("Core.Interop.NovusPresetFile.UnreadableXml", ex.Message));
        }

        var root = document.Root;

        if (root is null)
            return LoadOrderFileRead.Unreadable(Strings.Current["Core.Interop.NovusPresetFile.NoRootElement"]);

        var entries = new List<LoadOrderFileEntry>();

        // Direct children only, exactly as BUTR reads it. The root element's name is deliberately not
        // checked: BUTR does not check it either, and requiring "Preset" would reject files that work.
        foreach (var element in root.Elements("PresetModule"))
        {
            var id = element.Attribute("Id")?.Value?.Trim();

            if (string.IsNullOrEmpty(id))
                continue;

            entries.Add(new LoadOrderFileEntry(
                id,
                element.Attribute("RequiredVersion")?.Value?.Trim() ?? string.Empty,
                IsEnabled: true,
                Name: null,
                Url: Url(element)));
        }

        return entries.Count == 0
            ? LoadOrderFileRead.Unreadable(Strings.Current["Core.Interop.NovusPresetFile.NoPresetModules"])
            : LoadOrderInterop.Collect(entries);
    }

    private static string? Url(XElement element)
    {
        var declared = element.Attribute("URL")?.Value?.Trim();

        return string.IsNullOrEmpty(declared) ? null : declared;
    }

    // XmlWriter takes its declared encoding from the TextWriter, and StringWriter reports UTF-16,
    // which would put encoding="utf-16" on a file that is then saved as UTF-8.
    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
}
