using System.Xml;
using System.Xml.Linq;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

public enum ManifestFaultKind
{
    // <Xmls><XmlNode> with no <XmlName> child.
    XmlNodeNoName,

    // <XmlName> with no id attribute.
    XmlNameNoId,

    // <XmlName> with no path attribute.
    XmlNameNoPath,

    // A child element of <IncludedGameTypes> with no value attribute.
    GameTypeNoValue,

    // A <SubModule> with no <Name> element, or one with no value attribute.
    SubModuleNoName,

    // A <SubModule> with no <DLLName> element, or one with no value attribute.
    SubModuleNoDllName,

    // A <SubModule> with no <SubModuleClassType> element, or one with no value attribute.
    SubModuleNoClassType
}

// One place a module's SubModule.xml leaves out something the game's loader requires. Line is 1-based, and
// Element names the element the way the file does, for the row to quote.
public sealed record ManifestFault(ModuleId ModuleId, string DisplayName, ManifestFaultKind Kind, int Line, string Element)
{
    public string Describe() => Strings.Current[Kind switch
    {
        ManifestFaultKind.XmlNodeNoName => "Core.Modules.ManifestFault.XmlNodeNoName",
        ManifestFaultKind.XmlNameNoId => "Core.Modules.ManifestFault.XmlNameNoId",
        ManifestFaultKind.XmlNameNoPath => "Core.Modules.ManifestFault.XmlNameNoPath",
        ManifestFaultKind.SubModuleNoName => "Core.Modules.ManifestFault.SubModuleNoName",
        ManifestFaultKind.SubModuleNoDllName => "Core.Modules.ManifestFault.SubModuleNoDllName",
        ManifestFaultKind.SubModuleNoClassType => "Core.Modules.ManifestFault.SubModuleNoClassType",
        _ => "Core.Modules.ManifestFault.GameTypeNoValue"
    }];
}

// What the game's loader demands of a SubModule.xml that BEM's own, more forgiving, parser lets through.
//
// TaleWorlds.ObjectSystem XmlResource.cs GetXmlListAndApply reads every <Xmls><XmlNode> of each module the
// game loads and throws when the <XmlName> child is missing, or lacks its id or path attribute, or when a
// child of <IncludedGameTypes> lacks its value attribute. TaleWorlds.MountAndBlade Module.cs LoadSubModules
// calls it for every module in a loop with a catch that shows the "XML Error in module" box and rethrows,
// so the game does not start. ModuleXmlWellFormedness only asks whether the file parses; every fault here
// is a file that parses.
public static class ModuleXmlStructure
{
    public static IReadOnlyList<ManifestFault> Inspect(IReadOnlyList<ModuleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var found = new List<ManifestFault>();

        foreach (var entry in entries)
        {
            if (!entry.IsEnabled || entry.Manifest is not { } manifest || string.IsNullOrWhiteSpace(manifest.ManifestPath))
                continue;

            if (Load(manifest.ManifestPath) is not { } root)
                continue;

            found.AddRange(XmlNodeFaults(entry, root));
        }

        return found;
    }

    // What the game does with a <SubModule> that lacks a required element, read from the decompiled game:
    // TaleWorlds.ModuleManager SubModuleInfo.cs LoadFrom reads Name, DLLName and SubModuleClassType with
    // SelectSingleNode(...).Attributes["value"], which throws a NullReferenceException when the element or
    // its value attribute is missing. ModuleInfo.cs catches it and still adds the half-built SubModuleInfo
    // to SubModules, whose Tags are then empty (so it is never gated out) and whose DLLName (Name or DLLName
    // missing) or Assemblies (SubModuleClassType missing, since Assemblies is assigned after it) is null.
    // Module.cs LoadSubModules then runs Path.Combine(path, subModule.DLLName) or iterates
    // subModule.Assemblies outside any try, and the exception ends the boot.
    public static IReadOnlyList<ManifestFault> InspectSubModules(IReadOnlyList<ModuleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var found = new List<ManifestFault>();

        foreach (var entry in entries)
        {
            if (!entry.IsEnabled || entry.Manifest is not { } manifest || string.IsNullOrWhiteSpace(manifest.ManifestPath))
                continue;

            if (Load(manifest.ManifestPath) is not { } root)
                continue;

            foreach (var subModule in root.Element("SubModules")?.Elements("SubModule") ?? [])
            {
                // The game stops at the first one it cannot read, in the order LoadFrom reads them.
                (string Element, ManifestFaultKind Kind)[] required =
                [
                    ("Name", ManifestFaultKind.SubModuleNoName),
                    ("DLLName", ManifestFaultKind.SubModuleNoDllName),
                    ("SubModuleClassType", ManifestFaultKind.SubModuleNoClassType)
                ];

                foreach (var (element, kind) in required)
                {
                    if (subModule.Element(element)?.Attribute("value") is not null)
                        continue;

                    found.Add(Fault(entry, kind, subModule, element));
                    break;
                }
            }
        }

        return found;
    }

    // XmlResource.GetXmlListAndApply starts at the document's Module element and reads Xmls/XmlNode, so
    // a file whose root is anything else is not this check's business.
    private static XElement? Load(string path)
    {
        try
        {
            var root = XDocument.Load(path, LoadOptions.SetLineInfo).Root;

            return root is { Name.LocalName: "Module" } ? root : null;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<ManifestFault> XmlNodeFaults(ModuleEntry entry, XElement root)
    {
        foreach (var node in root.Elements("Xmls").Elements("XmlNode"))
        {
            if (node.Element("XmlName") is not { } name)
            {
                yield return Fault(entry, ManifestFaultKind.XmlNodeNoName, node, "XmlNode");
                continue;
            }

            // The game throws on the first of these it meets in an XmlNode, so the id is reported before
            // the path and a node with neither says so once.
            if (name.Attribute("id") is null)
                yield return Fault(entry, ManifestFaultKind.XmlNameNoId, name, "XmlName");
            else if (name.Attribute("path") is null)
                yield return Fault(entry, ManifestFaultKind.XmlNameNoPath, name, "XmlName");

            foreach (var type in node.Element("IncludedGameTypes")?.Elements() ?? [])
            {
                if (type.Attribute("value") is null)
                    yield return Fault(entry, ManifestFaultKind.GameTypeNoValue, type, type.Name.LocalName);
            }
        }
    }

    private static ManifestFault Fault(ModuleEntry entry, ManifestFaultKind kind, XElement at, string element) =>
        new(entry.Id, entry.DisplayName, kind, ((IXmlLineInfo)at).HasLineInfo() ? ((IXmlLineInfo)at).LineNumber : 0, element);
}
