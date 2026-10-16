namespace BannerlordEnvironmentManager.Core.Localization;

public sealed record LanguageCatalog(
    string Code,
    string NativeName,
    string EnglishName,
    IReadOnlyDictionary<string, LanguageEntry> Entries)
{
    public static LanguageCatalog Empty(string code) =>
        new(code, code, code, new Dictionary<string, LanguageEntry>());
}
