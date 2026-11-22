using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BannerlordEnvironmentManager.Core.Install;

namespace BannerlordEnvironmentManager.Core.Nexus;

// A mod author states where the mod lives in more than one place. The top-level <Url> is the one
// everything reads, but BUTR's schema also defines <UpdateInfo value="NexusMods:2018" />, and some
// authors put the page inside a private element of their own such as <ISKL><NexusId value="10376" />.
//
// All of them are the author's own words about their own mod, so all of them are facts rather than
// matches. Only the module's own manifest is read: a readme naming somebody else's mod page is a
// different kind of claim entirely and is not treated as one of these.
public static partial class ManifestNexusId
{
    [GeneratedRegex(@"NexusMods\s*:\s*(?<mod>\d{1,7})", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateInfoPattern { get; }

    // BEM keeps a copy of a manifest before it rewrites one, and at least one real module
    // has an id in that copy which the live file no longer has. Where the mod lives does not change
    // between versions, so an older copy of the author's own words is still the author's own words.
    public const string BackupSuffix = ".bem.bak";

    public static int? TryRead(string? manifestPath) =>
        ReadFile(manifestPath) ?? ReadFile(manifestPath is null ? null : manifestPath + BackupSuffix);

    private static int? ReadFile(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            return null;

        XDocument document;

        try
        {
            document = XDocument.Load(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }

        return TryRead(document);
    }

    // A manifest declaring two different pages is one BEM cannot read a single answer out of, so it
    // yields none rather than the first one found.
    private static int? TryRead(XDocument document)
    {
        var found = new HashSet<int>();

        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                var value = attribute.Value;

                if (NexusArchiveName.TryGetModIdFromUrl(value) is { } fromUrl)
                    found.Add(fromUrl);

                if (element.Name.LocalName.Equals("NexusId", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var declared)
                    && declared > 0)
                    found.Add(declared);

                if (UpdateInfoPattern.Match(value) is { Success: true } update
                    && int.TryParse(update.Groups["mod"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromUpdateInfo)
                    && fromUpdateInfo > 0)
                    found.Add(fromUpdateInfo);
            }
        }

        return found.Count == 1 ? found.Single() : null;
    }
}
