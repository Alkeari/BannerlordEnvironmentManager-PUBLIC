using System.Xml;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public sealed record MalformedModuleXml(ModuleId ModuleId, string ModuleName, string Path, int Line, int Column, string Error)
{
    // Stable across runs and languages, so an acceptance survives a rescan. The parser's message is left
    // out because it follows the system language.
    public string Subject => $"{Path}:{Line}:{Column}";
}

public sealed record ModuleXmlReport(IReadOnlyList<MalformedModuleXml> Findings, int FilesRead, IReadOnlyList<string> Problems);

// Every XML file an enabled mod ships, read for one thing: does it parse. A file that does not stops the
// game at whatever moment it reads that file, and the crash then names the game's XML loader, not the mod.
// Well-formedness only, so no version of the game changes the answer; schemas are XmlMergeFaults' work.
//
// Disproof condition: a mod can ship a broken XML the game never opens, so a row means "could", and a
// person opens the named line to judge it. Nothing is resolved from the network and no DTD is processed.
public static class ModuleXmlCheck
{
    public static ModuleXmlReport Inspect(IEnumerable<(ModuleId Id, string Name, string Folder)> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var findings = new List<MalformedModuleXml>();
        var problems = new List<string>();
        var read = 0;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            CheckCharacters = true
        };

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        foreach (var module in modules)
        {
            if (!Directory.Exists(module.Folder))
                continue;

            foreach (var path in Directory.EnumerateFiles(module.Folder, "*.xml", options))
            {
                // AssetPackages holds the engine's packed resource indexes, which the engine reads with its own
                // parser and which carry several root elements by design: War Sails' own nested_prefabs_packed.xml
                // does, so reading one as a broken XML file is a wrong finding.
                if (IsUnderAssetPackages(module.Folder, path))
                    continue;

                try
                {
                    // As text, the way the game's own loader reads them: it decodes the file first, so an encoding
                    // declaration that disagrees with the bytes (common in language files, the game's included)
                    // is never read as a fault.
                    using var text = new StringReader(File.ReadAllText(path));
                    using var reader = XmlReader.Create(text, settings);

                    while (reader.Read())
                    {
                    }

                    read++;
                }
                catch (XmlException ex)
                {
                    read++;
                    findings.Add(new MalformedModuleXml(module.Id, module.Name, path, ex.LineNumber, ex.LinePosition, ex.Message));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{path}: {ex.Message}");
                }
            }
        }

        return new ModuleXmlReport(
            [.. findings.OrderBy(f => f.ModuleName, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)],
            read,
            problems);
    }

    internal static bool IsUnderAssetPackages(string moduleFolder, string path) =>
        Path.GetRelativePath(moduleFolder, path)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
            .Contains("AssetPackages", StringComparer.OrdinalIgnoreCase);
}
