namespace BannerlordEnvironmentManager.Core.Install;

// Which version a quarantined folder belongs to is a question about the path it came out of, not about
// the store it sits in. The store stays machine-wide on purpose: it holds folders taken out of a game
// install and has to put each one back at the absolute origin it wrote down, so splitting it per
// version would strand every folder already in it. Scoping happens on the way out instead.
//
// Without this, "keep the five most recent replaced module folders" counted across every installed
// version at once, and five installs on one version permanently deleted five kept folders belonging to
// another with no undo. The same gap listed both versions' entries side by side with nothing to tell
// them apart.
public static class QuarantineScope
{
    // A null or blank root means every version, which is what a caller asking for the whole store
    // passes. It is never what a caller that is about to delete something passes.
    public static bool IsUnder(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return true;

        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var full = Path.GetFullPath(path);
            var prefix = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return full.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // A hand-edited index can hold a path Windows will not resolve. It belongs to no version
            // that can be named, so it is left out rather than counted against the one on screen.
            return false;
        }
    }

    // The modules folder and the Steam workshop folder are both scanned as one install's content, so an
    // entry from either belongs to the version being shown. An empty list means every version.
    public static bool IsUnderAny(string? path, IReadOnlyList<string>? roots) =>
        roots is null || roots.Count == 0 || roots.Any(root => IsUnder(path, root));
}
