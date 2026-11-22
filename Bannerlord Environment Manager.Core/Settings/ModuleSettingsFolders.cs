using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Settings;

// Which settings folders belong to one mod, so a mod's own row can show them instead of sending the
// player to a page listing every folder. A folder with no match belongs to no mod and is not returned.
public static class ModuleSettingsFolders
{
    public static IReadOnlyList<SettingsFolderReview> For(IEnumerable<SettingsFolderReview> reviews, ModuleId module)
    {
        ArgumentNullException.ThrowIfNull(reviews);

        return reviews
            .Where(review => review.Kind != SettingsMatchKind.None && review.MatchedModuleId == module)
            .OrderBy(review => review.Kind)
            .ThenBy(review => review.Folder.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // His rule: a mod row shows Settings only when we definitely hold a settings file for it. A partial
    // name match is a guess, so it never counts; an attribution the player confirmed does.
    public static bool HasDefinite(IEnumerable<SettingsFolderReview> reviews, ModuleId module) =>
        For(reviews, module).Any(review => review.Kind != SettingsMatchKind.Partial && review.Folder.FileCount > 0);

    // The files in a settings folder a player can edit: XML and JSON, the two formats MCM writes.
    public static IReadOnlyList<string> EditableFiles(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return [];

        return [.. Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
            .Where(file => McmSettingsFile.IsXml(file) || McmSettingsFile.IsJson(file))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)];
    }

    // The schema that describes one settings file: the class whose Id is the file's name, else the
    // only class whose folder name is the file's folder. Null means the raw editor is the fallback.
    public static McmSettingsSchema? SchemaFor(string filePath, IReadOnlyList<McmSettingsSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        var stem = Path.GetFileNameWithoutExtension(filePath);
        var folder = Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty);

        var byId = schemas.FirstOrDefault(schema => schema.Id.Equals(stem, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
            return byId;

        var byFolder = schemas.Where(schema => schema.FolderName.Length > 0
            && schema.FolderName.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList();
        return byFolder.Count == 1 ? byFolder[0] : null;
    }

    public static void Save(string filePath, IReadOnlyDictionary<string, string> edits) =>
        McmSettingsFile.Write(filePath, edits);

    // The raw editor's save: written whole to a sibling file first, then swapped in, so a failed write
    // never leaves half a settings file. XML or JSON that does not parse is refused, since the mod would
    // throw it away.
    public static void Save(string path, string text)
    {
        if (McmSettingsFile.IsJson(path))
            using (System.Text.Json.JsonDocument.Parse(text)) { }
        else if (McmSettingsFile.IsXml(path))
            System.Xml.Linq.XDocument.Parse(text);

        var temp = path + ".bem-tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
