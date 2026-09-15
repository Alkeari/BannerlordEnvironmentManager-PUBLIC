using System.Net;
using System.Text.RegularExpressions;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// ButterLib renders its crash report as an HTML document, so the exception a parser needs is behind
// markup, entities and an embedded copy of the report as JSON. The JSON copy contains frame text of
// its own, which would be read as a second stack if the script block were left in.
public static partial class CrashReportText
{
    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\s*\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle { get; }

    [GeneratedRegex(@"<\s*/?\s*(br|p|div|li|tr|td|th|pre|h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTag { get; }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex AnyTag { get; }

    [GeneratedRegex(@"<\s*(!doctype|html|head|body)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlDocument { get; }

    public static bool IsHtml(string? content) => content is not null && HtmlDocument.IsMatch(content);

    public static string Extract(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        if (!IsHtml(content))
            return content;

        var text = ScriptOrStyle.Replace(content, "\n");
        text = BlockTag.Replace(text, "\n");
        text = AnyTag.Replace(text, string.Empty);

        return WebUtility.HtmlDecode(text);
    }
}
