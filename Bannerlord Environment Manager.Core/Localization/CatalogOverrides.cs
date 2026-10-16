namespace BannerlordEnvironmentManager.Core.Localization;

public sealed record OverrideLoad(
    IReadOnlyDictionary<string, LanguageCatalog> Catalogs,
    IReadOnlyList<string> Problems);

public static class CatalogOverrides
{
    public const string FolderName = "Languages";

    public static OverrideLoad Load(string directory)
    {
        var catalogs = new Dictionary<string, LanguageCatalog>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        if (!Directory.Exists(directory))
            return new OverrideLoad(catalogs, problems);

        string[] files;

        try
        {
            files = Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException)
        {
            problems.Add($"{Path.GetFileName(directory)}: {ex.Message}");
            return new OverrideLoad(catalogs, problems);
        }

        foreach (var file in files)
        {
            string json;

            try
            {
                json = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(file);

            // A file names its language one of two ways: a "$language" header, or a file name that
            // matches a language this build ships. The header is read first and on its own, so a
            // three-line correction still needs no header while a stray backup or note dropped in
            // the folder, which names no language either way, is reported rather than offered as
            // one. The second read costs a parse of a small file at startup and only happens for a
            // correction with no header.
            var result = CatalogReader.Read(json, null);

            if (result.Catalog is null && Ships(name))
                result = CatalogReader.Read(json, name);

            if (result.Catalog is null)
            {
                problems.Add($"{Path.GetFileName(file)}: {result.Error}");
                continue;
            }

            if (!LooksLikeLanguageCode(result.Catalog.Code))
            {
                problems.Add(
                    $"{Path.GetFileName(file)}: '{result.Catalog.Code}' is not a language code.");
                continue;
            }

            // Files are read in ordinal path order, and the last one for a given language code
            // wins any earlier one: two community translations declaring the same code are not
            // an error, just a silent overwrite in that order.
            catalogs[result.Catalog.Code] = result.Catalog;
        }

        return new OverrideLoad(catalogs, problems);
    }

    private static bool Ships(string name) =>
        EmbeddedCatalogs.Codes.Contains(name, StringComparer.OrdinalIgnoreCase);

    // The shape of a language tag: a two or three letter language, then any number of subtags of
    // two to eight letters or digits. This is looser than the set of tags Windows knows, because a
    // community translation into a language BEM has never heard of is the point of the folder, and
    // tighter than "any file name", which is what let a notes.json become a language in the picker.
    private static bool LooksLikeLanguageCode(string code)
    {
        var subtags = code.Split('-');

        if (subtags[0].Length is < 2 or > 3 || !subtags[0].All(char.IsAsciiLetter))
            return false;

        return subtags.Skip(1).All(
            subtag => subtag.Length is >= 2 and <= 8 && subtag.All(char.IsAsciiLetterOrDigit));
    }
}
