namespace BannerlordEnvironmentManager.Core.Localization;

public abstract record LanguageEntry
{
    public sealed record Single(string Value) : LanguageEntry;

    public sealed record Plural(IReadOnlyDictionary<PluralCategory, string> Forms) : LanguageEntry;
}
