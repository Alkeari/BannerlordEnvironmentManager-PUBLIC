using System.Text;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// The one place that knows what a "divider-<slug>" id looks like, confirmed against a real Novus
// preset in the wild (1.4.7_Vanilla_Plus_Load_Order.xml: divider-core, divider-big-mods,
// divider-graphics, and fifteen more). Every recognition and generation point in this codebase -
// import, export, the live install scan, snapshot restore - goes through this class rather than
// matching the prefix itself, so the convention only ever has to be right in one place.
public static class DividerConvention
{
    public const string Prefix = "divider-";

    // "divider-big-mods" -> "Big Mods". Anything that does not fit the shape (no prefix, nothing
    // after it, or a suffix with no letters or digits at all) is not a divider id, so this returns
    // null rather than an empty or nonsensical label.
    public static string? TryParseLabel(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var slug = id[Prefix.Length..].Trim();

        if (slug.Length == 0 || !slug.Any(char.IsLetterOrDigit))
            return null;

        var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
            return null;

        var label = new StringBuilder();

        foreach (var word in words)
        {
            if (label.Length > 0)
                label.Append(' ');

            label.Append(char.ToUpperInvariant(word[0]));

            if (word.Length > 1)
                label.Append(word[1..].ToLowerInvariant());
        }

        return label.ToString();
    }

    // "Big Mods" -> "divider-big-mods". Collision-suffixed against idInUse with "-2", "-3", ... the
    // same pattern LoadOrderProfileStore.Reserve already uses for filenames, so two sections with the
    // same name still export as two distinct, re-importable ids.
    public static string ToId(string label, Func<string, bool> idInUse)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(idInUse);

        var slug = new StringBuilder();
        var lastWasDash = true;

        foreach (var ch in label.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                slug.Append(char.ToLowerInvariant(ch));
                lastWasDash = false;
            }
            else if (!lastWasDash && slug.Length > 0)
            {
                slug.Append('-');
                lastWasDash = true;
            }
        }

        while (slug.Length > 0 && slug[^1] == '-')
            slug.Length--;

        var stem = slug.Length == 0 ? "section" : slug.ToString();
        var candidate = Prefix + stem;
        var counter = 2;

        while (idInUse(candidate))
            candidate = $"{Prefix}{stem}-{counter++}";

        return candidate;
    }
}
