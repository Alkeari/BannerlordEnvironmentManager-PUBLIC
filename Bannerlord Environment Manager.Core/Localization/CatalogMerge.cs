namespace BannerlordEnvironmentManager.Core.Localization;

public static class CatalogMerge
{
    public static LanguageCatalog Overlay(LanguageCatalog under, LanguageCatalog over)
    {
        var entries = new Dictionary<string, LanguageEntry>(under.Entries, StringComparer.Ordinal);

        foreach (var (key, entry) in over.Entries)
            entries[key] = entry;

        return under with { Entries = entries };
    }
}
