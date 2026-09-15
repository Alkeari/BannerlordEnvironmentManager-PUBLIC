using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BannerlordEnvironmentManager.Core.Modules;

public static partial class SubModuleXmlParser
{
    [GeneratedRegex("""<Id\s+value\s*=\s*"(?<id>[^"]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex DeclaredIdPattern { get; }

    public static string? TryRecoverDeclaredId(string manifestPath)
    {
        try
        {
            var text = StripComments(File.ReadAllText(manifestPath));
            var match = DeclaredIdPattern.Match(text);
            return match.Success ? match.Groups["id"].Value.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string StripComments(string text)
    {
        var result = new StringBuilder(text.Length);
        var index = 0;

        while (true)
        {
            var start = text.IndexOf("<!--", index, StringComparison.Ordinal);

            if (start < 0)
            {
                result.Append(text, index, text.Length - index);
                break;
            }

            result.Append(text, index, start - index);
            var end = text.IndexOf("-->", start + 4, StringComparison.Ordinal);

            if (end < 0)
                break;

            index = end + 3;
        }

        return result.ToString();
    }

    // DepotDownloader, and downloaders like it, preallocate a destination file's full length before
    // writing its bytes, so a SubModule.xml can sit at zero length or entirely NUL bytes for a moment
    // while the rest of the archive fills in around it. That shape is not corruption, it is "not
    // written yet"; the window is 30 seconds, long enough that a rescan while the download is still
    // moving sees the same transient shape and skips it again, short enough that a file left empty
    // past it is treated as settled and reported like any other manifest that fails to parse.
    private static readonly TimeSpan PartialWriteWindow = TimeSpan.FromSeconds(30);

    public static bool LooksLikePartialWrite(string manifestPath)
    {
        byte[] content;
        DateTime lastWriteUtc;

        try
        {
            content = File.ReadAllBytes(manifestPath);
            lastWriteUtc = File.GetLastWriteTimeUtc(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return LooksLikePartialWrite(content, lastWriteUtc, DateTime.UtcNow);
    }

    public static bool LooksLikePartialWrite(byte[] content, DateTime lastWriteTimeUtc, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (nowUtc - lastWriteTimeUtc > PartialWriteWindow)
            return false;

        return content.Length == 0 || Array.TrueForAll(content, b => b == 0);
    }

    public static bool TryLoad(string manifestPath, out ModuleManifest manifest, out string? error)
    {
        manifest = null!;
        error = null;

        try
        {
            var document = XDocument.Load(manifestPath);
            var folderPath = Path.GetDirectoryName(manifestPath) ?? string.Empty;
            var parsed = Parse(document, manifestPath, folderPath);

            if (parsed.Id.IsEmpty)
            {
                error = "SubModule.xml has no <Id value=\"...\"/> element.";
                return false;
            }

            manifest = parsed;
            return true;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static ModuleManifest Parse(XDocument document, string manifestPath, string folderPath)
    {
        var root = document.Root ?? new XElement("Module");

        var dependencies = new Dictionary<ModuleId, ModuleDependency>();

        foreach (var element in root.Element("DependedModules")?.Elements("DependedModule") ?? [])
        {
            Merge(dependencies, new ModuleDependency(
                new ModuleId(element.Attribute("Id")?.Value ?? string.Empty),
                DependencyOrder.LoadBeforeThis,
                ModuleVersionRange.Parse(element.Attribute("DependentVersion")?.Value),
                IsTrue(element.Attribute("Optional")?.Value),
                IsIncompatible: false));
        }

        foreach (var element in root.Element("DependedModuleMetadatas")?.Elements("DependedModuleMetadata") ?? [])
        {
            var order = ParseOrder(element.Attribute("order")?.Value);

            Merge(dependencies, new ModuleDependency(
                new ModuleId(element.Attribute("id")?.Value ?? string.Empty),
                order,
                ModuleVersionRange.Parse(element.Attribute("version")?.Value),
                IsTrue(element.Attribute("optional")?.Value),
                IsTrue(element.Attribute("incompatible")?.Value),
                IsOrderExplicit: order != DependencyOrder.None));
        }

        foreach (var element in root.Element("ModulesToLoadAfterThis")?.Elements("Module") ?? [])
        {
            Merge(dependencies, new ModuleDependency(
                new ModuleId(element.Attribute("Id")?.Value ?? string.Empty),
                DependencyOrder.LoadAfterThis,
                ModuleVersionRange.Any,
                IsOptional: false,
                IsIncompatible: false,
                IsOrderExplicit: true));
        }

        // The mirror of ModulesToLoadAfterThis. No module on a real install uses it, but a format
        // that reads one direction and silently drops the other would lose a stated requirement.
        foreach (var element in root.Element("ModulesToLoadBeforeThis")?.Elements("Module") ?? [])
        {
            Merge(dependencies, new ModuleDependency(
                new ModuleId(element.Attribute("Id")?.Value ?? string.Empty),
                DependencyOrder.LoadBeforeThis,
                ModuleVersionRange.Any,
                IsOptional: false,
                IsIncompatible: false,
                IsOrderExplicit: true));
        }

        var versionText = Value(root.Element("Version"))?.Trim();
        var declared = new List<DeclaredAssembly>();

        foreach (var subModule in root.Element("SubModules")?.Elements("SubModule") ?? [])
        {
            IReadOnlyList<XElement> tags = [.. subModule.Element("Tags")?.Elements("Tag") ?? []];

            var gate = new SubModuleGate(
                Tag(tags, "DedicatedServerType").FirstOrDefault(),
                Tag(tags, "RejectedPlatform"),
                Tag(tags, "ExclusivePlatform"));

            Declare(declared, Value(subModule.Element("DLLName")), gate);

            foreach (var assembly in subModule.Element("Assemblies")?.Elements("Assembly") ?? [])
                Declare(declared, Value(assembly), gate);

            // BUTR's module loader picks one of several per-game-version builds out of the same bin
            // folder by wildcard, so those files are named by the manifest without any one of them
            // appearing in it. Twelve modules on a real install name their builds this way.
            foreach (var filter in Tag(tags, "LoaderFilter"))
                Declare(declared, filter, gate);
        }

        return new ModuleManifest(
            new ModuleId(Value(root.Element("Id")) ?? string.Empty),
            Value(root.Element("Name")) ?? string.Empty,
            ModuleVersion.Parse(versionText),
            ParseType(Value(root.Element("ModuleType"))),
            ParseCategory(root),
            folderPath,
            manifestPath,
            [.. dependencies.Values],
            Url: ParseUrl(root),
            VersionText: string.IsNullOrWhiteSpace(versionText) ? null : versionText,
            DeclaredAssemblies: declared);
    }

    private static IReadOnlyList<string> Tag(IReadOnlyList<XElement> tags, string key) =>
    [
        .. tags
            .Where(t => string.Equals(t.Attribute("key")?.Value, key, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Attribute("value")?.Value?.Trim())
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
    ];

    private static void Declare(List<DeclaredAssembly> declared, string? name, SubModuleGate gate)
    {
        var trimmed = name?.Trim();

        if (!string.IsNullOrEmpty(trimmed))
            declared.Add(new DeclaredAssembly(trimmed, gate));
    }

    // This value ends up handed to the shell by "Open mod page", so a manifest declaring a local
    // path or a script scheme must not become a launchable command.
    private static string? ParseUrl(XElement root)
    {
        var declared = Value(root.Element("Url"))?.Trim();

        return Uri.TryCreate(declared, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? declared
            : null;
    }

    private static void Merge(Dictionary<ModuleId, ModuleDependency> target, ModuleDependency dependency)
    {
        if (dependency.TargetId.IsEmpty)
            return;

        if (!target.TryGetValue(dependency.TargetId, out var existing))
        {
            target[dependency.TargetId] = dependency;
            return;
        }

        target[dependency.TargetId] = existing with
        {
            Order = existing.Order != DependencyOrder.None ? existing.Order : dependency.Order,
            // True when any declaration that agrees with the order being kept stated it outright. A module
            // commonly names the same target in DependedModules and again in DependedModuleMetadatas with
            // an order attribute; the first is what sets the direction, but the second is the author
            // writing the requirement down and must not be lost.
            IsOrderExplicit = existing.Order != DependencyOrder.None
                ? existing.IsOrderExplicit || (dependency.Order == existing.Order && dependency.IsOrderExplicit)
                : dependency.IsOrderExplicit,
            VersionRange = !existing.VersionRange.IsAny ? existing.VersionRange : dependency.VersionRange,
            IsOptional = existing.IsOptional && dependency.IsOptional,
            IsIncompatible = existing.IsIncompatible || dependency.IsIncompatible
        };
    }

    private static string? Value(XElement? element) =>
        element?.Attribute("value")?.Value ?? element?.Value;

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static DependencyOrder ParseOrder(string? value) => value switch
    {
        not null when value.Equals("LoadBeforeThis", StringComparison.OrdinalIgnoreCase) => DependencyOrder.LoadBeforeThis,
        not null when value.Equals("LoadAfterThis", StringComparison.OrdinalIgnoreCase) => DependencyOrder.LoadAfterThis,
        _ => DependencyOrder.None
    };

    private static ModuleType ParseType(string? value) => value switch
    {
        not null when value.Equals("Official", StringComparison.OrdinalIgnoreCase) => ModuleType.Official,
        not null when value.Equals("OfficialOptional", StringComparison.OrdinalIgnoreCase) => ModuleType.OfficialOptional,
        _ => ModuleType.Community
    };

    private static ModuleCategory ParseCategory(XElement root)
    {
        var declared = Value(root.Element("ModuleCategory"));

        if (declared is not null)
        {
            if (declared.Equals("Multiplayer", StringComparison.OrdinalIgnoreCase))
                return ModuleCategory.Multiplayer;
            if (declared.Equals("Singleplayer", StringComparison.OrdinalIgnoreCase))
                return ModuleCategory.Singleplayer;
        }

        var single = IsTrue(Value(root.Element("SingleplayerModule")));
        var multi = IsTrue(Value(root.Element("MultiplayerModule")));

        return (single, multi) switch
        {
            (true, true) => ModuleCategory.Both,
            (false, true) => ModuleCategory.Multiplayer,
            _ => ModuleCategory.Singleplayer
        };
    }
}
