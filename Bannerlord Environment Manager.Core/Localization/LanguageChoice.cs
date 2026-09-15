using System.Globalization;

namespace BannerlordEnvironmentManager.Core.Localization;

public static class LanguageChoice
{
    public const string Fallback = "en";

    public static string Resolve(
        LanguagePreference saved, IReadOnlyCollection<string> available, CultureInfo system)
    {
        if (saved.Code is { Length: > 0 } code)
        {
            var match = available.FirstOrDefault(
                c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        var exact = available.FirstOrDefault(
            c => string.Equals(c, system.Name, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
            return exact;

        var byLanguage = available.FirstOrDefault(c =>
        {
            try
            {
                return string.Equals(
                    CultureInfo.GetCultureInfo(c).TwoLetterISOLanguageName,
                    system.TwoLetterISOLanguageName,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (CultureNotFoundException)
            {
                // Unrecognized culture code; skip it and continue with the next candidate.
                return false;
            }
        });

        return byLanguage ?? Fallback;
    }
}
