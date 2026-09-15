using System.Text;

namespace BannerlordEnvironmentManager.Core.Interop;

// The BUTR .bmlist grammar: one "{Id}: {Version}" line per enabled module, in load order, and
// nothing else. The reference reader splits on the two characters ": " and drops any line that does
// not yield exactly two parts, so a header, a comment or a blank-line separator either vanishes or,
// if it happens to contain one ": ", arrives in BLSE as a module id that does not exist.
public static class BmListFile
{
    public const string Separator = ": ";

    // An empty version collapses the reference reader's split and deletes the entry outright, so a
    // module BEM could not read a version for is written with this rather than dropped from the order.
    public const string UnknownVersion = "v0.0.0";

    public static string Write(IReadOnlyList<LoadOrderFileEntry> entries) => Describe(entries).Content;

    public static LoadOrderFileWrite Describe(IReadOnlyList<LoadOrderFileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var text = new StringBuilder();
        var unrepresentable = new List<string>();

        foreach (var entry in entries)
        {
            var id = entry.Id?.Trim() ?? string.Empty;

            if (id.Length == 0)
                continue;

            if (id.Contains(Separator, StringComparison.Ordinal) || id.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                unrepresentable.Add(entry.Id!);
                continue;
            }

            var version = entry.Version?.Trim() ?? string.Empty;

            text.Append(id).Append(Separator).Append(version.Length == 0 ? UnknownVersion : version).Append("\r\n");
        }

        return new LoadOrderFileWrite(text.ToString(), unrepresentable);
    }

    public static LoadOrderFileRead Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var entries = new List<LoadOrderFileEntry>();
        var unreadable = 0;

        // The line is searched before it is trimmed: the trailing space of "Native: " is half the
        // separator, and trimming it away first would turn an entry with no version into a dropped line.
        foreach (var line in text.Split(['\r', '\n']))
        {
            if (line.Trim().Length == 0)
                continue;

            var separator = line.IndexOf(Separator, StringComparison.Ordinal);

            // Split on the first ": " rather than demanding exactly two parts: BUTR drops a line with
            // more, and dropping it silently is what makes a module disappear from a shared order.
            if (separator < 0)
            {
                unreadable++;
                continue;
            }

            var id = line[..separator].Trim();

            if (id.Length == 0)
            {
                unreadable++;
                continue;
            }

            entries.Add(new LoadOrderFileEntry(id, line[(separator + Separator.Length)..].Trim()));
        }

        return LoadOrderInterop.Collect(entries, unreadable);
    }
}
