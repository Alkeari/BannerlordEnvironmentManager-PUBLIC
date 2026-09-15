using System.Reflection;

namespace BannerlordEnvironmentManager.Core.Localization;

// Shipped catalogs live inside the assembly rather than beside the exe because the release is a
// single self-contained file that users copy on its own. The Languages folder is an override layer,
// never the only copy of a shipped language.
public static class EmbeddedCatalogs
{
    private const string Prefix = "BannerlordEnvironmentManager.Core.Localization.Catalogs.";

    private static readonly Assembly Assembly = typeof(EmbeddedCatalogs).Assembly;

    public static IReadOnlyList<string> Codes { get; } =
    [
        .. Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)
                && name.EndsWith(".json", StringComparison.Ordinal))
            .Select(name => name[Prefix.Length..^".json".Length])
            .OrderBy(code => code, StringComparer.Ordinal),
    ];

    public static LanguageCatalog English() => Load("en") ?? LanguageCatalog.Empty("en");

    public static LanguageCatalog? Load(string code)
    {
        using var stream = Assembly.GetManifestResourceStream(Prefix + code + ".json");
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream);
        return CatalogReader.Read(reader.ReadToEnd()).Catalog;
    }
}
