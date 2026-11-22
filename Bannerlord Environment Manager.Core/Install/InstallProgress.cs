using System.Globalization;

namespace BannerlordEnvironmentManager.Core.Install;

// What the progress area says while an install runs, in one place, because it used to be said in two.
// The status line stated the work and appended a percentage with a hardcoded sign, and the caption
// built from that line appended the culture's own percentage after it, so every install read
// "MyMod - Extracting (42%)  42%". The bar renders the number, so the line states the work only.
public static class InstallProgress
{
    public static string StatusLine(string archiveLabel, string stageLabel) =>
        string.IsNullOrWhiteSpace(stageLabel) ? archiveLabel : $"{archiveLabel} - {stageLabel}";

    // The percentage is formatted by the culture rather than by a catalog key, since where the sign
    // sits is a property of the language and not a string anyone should have to translate.
    public static string Caption(string? statusMessage, int percent, bool indeterminate, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        var doing = (statusMessage ?? string.Empty).Split(Environment.NewLine)[0].Trim();

        if (indeterminate)
            return doing;

        var formatted = (percent / 100.0).ToString("P0", culture);

        return doing.Length == 0 ? formatted : $"{doing}  {formatted}";
    }
}
