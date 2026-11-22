namespace BannerlordEnvironmentManager.Core.Localization;

// Integer rules only. BEM counts modules, findings and files, never fractions, so the categories a
// language reaches only for fractional counts are left out of Select. Other is required everywhere
// regardless, because it is what an unrecognized language code resolves to.
public static class PluralRules
{
    private static readonly HashSet<PluralCategory> OtherOnly = [PluralCategory.Other];

    private static readonly HashSet<PluralCategory> OneOther =
        [PluralCategory.One, PluralCategory.Other];

    private static readonly HashSet<PluralCategory> Slavic =
        [PluralCategory.One, PluralCategory.Few, PluralCategory.Many, PluralCategory.Other];

    public static IReadOnlySet<PluralCategory> Categories(string languageCode) =>
        Family(languageCode) switch
        {
            Rule.None => OtherOnly,
            Rule.Russian or Rule.Polish => Slavic,
            _ => OneOther,
        };

    public static PluralCategory Select(string languageCode, long count) =>
        Family(languageCode) switch
        {
            Rule.None => PluralCategory.Other,
            Rule.ZeroIsSingular => count is 0 or 1 ? PluralCategory.One : PluralCategory.Other,
            Rule.Russian => Russian(count),
            Rule.Polish => Polish(count),
            _ => count == 1 ? PluralCategory.One : PluralCategory.Other,
        };

    private static PluralCategory Russian(long count)
    {
        var last = Math.Abs(count) % 10;
        var lastTwo = Math.Abs(count) % 100;

        if (last == 1 && lastTwo != 11)
            return PluralCategory.One;

        return last is >= 2 and <= 4 && lastTwo is < 12 or > 14
            ? PluralCategory.Few
            : PluralCategory.Many;
    }

    private static PluralCategory Polish(long count)
    {
        if (count == 1)
            return PluralCategory.One;

        var last = Math.Abs(count) % 10;
        var lastTwo = Math.Abs(count) % 100;

        return last is >= 2 and <= 4 && lastTwo is < 12 or > 14
            ? PluralCategory.Few
            : PluralCategory.Many;
    }

    private static Rule Family(string languageCode) => languageCode switch
    {
        "zh-Hans" or "ja" or "ko" => Rule.None,
        "ru" => Rule.Russian,
        "pl" => Rule.Polish,
        "fr" or "pt-BR" => Rule.ZeroIsSingular,
        "en" or "de" or "es" or "it" or "tr" => Rule.OneOther,
        _ => Rule.None,
    };

    private enum Rule
    {
        None,
        OneOther,
        ZeroIsSingular,
        Russian,
        Polish,
    }
}
