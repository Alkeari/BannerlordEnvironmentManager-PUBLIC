using System.Globalization;

namespace BannerlordEnvironmentManager.Core.Localization;

public sealed class Localizer(LanguageCatalog active, LanguageCatalog english) : ILocalizer
{
    public static Localizer EnglishOnly(LanguageCatalog english) => new(english, english);

    public string LanguageCode => active.Code;

    public CultureInfo Culture { get; } = CultureFor(active.Code);

    public string this[string key] =>
        Lookup(key) is LanguageEntry.Single single ? single.Value : key;

    public string Format(string key, params object?[] args)
    {
        if (Lookup(key) is not LanguageEntry.Single single)
            return key;

        return Render(single.Value, key, args);
    }

    public string Plural(string key, long count, params object?[] args)
    {
        object?[] all = [count, .. args];

        return Lookup(key) switch
        {
            LanguageEntry.Single single => Render(single.Value, key, all),
            LanguageEntry.Plural plural =>
                Form(plural, count) is string form ? Render(form, key, all) : key,
            _ => key,
        };
    }

    private string? Form(LanguageEntry.Plural plural, long count)
    {
        var category = PluralRules.Select(LanguageCode, count);

        if (plural.Forms.TryGetValue(category, out var form))
            return form;

        return plural.Forms.TryGetValue(PluralCategory.Other, out var other) ? other : null;
    }

    // A translator can write a template BEM never passes enough arguments for. Showing the key is the
    // same failure mode as a missing key, and both beat a FormatException reaching the UI thread.
    private string Render(string template, string key, object?[] args)
    {
        try
        {
            return args.Length == 0 ? template : string.Format(Culture, template, args);
        }
        catch (FormatException)
        {
            return key;
        }
    }

    private LanguageEntry? Lookup(string key) =>
        active.Entries.TryGetValue(key, out var entry) ? entry
        : english.Entries.TryGetValue(key, out var fallback) ? fallback
        : null;

    private static CultureInfo CultureFor(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
