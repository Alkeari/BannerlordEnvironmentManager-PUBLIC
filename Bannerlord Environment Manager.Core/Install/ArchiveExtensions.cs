namespace BannerlordEnvironmentManager.Core.Install;

// The one place BEM writes down which file extensions count as a mod archive. It used to be written
// twice - once in InstallViewModel's own install/watch/extract dispatch, once in Services'
// RecycleBinArchiveNames for the deleted-archive sweep - and the two drifted: InstallViewModel widened
// to cover compressed tarballs while RecycleBinArchiveNames stayed at zip/7z/rar, so a deleted .tar.gz
// silently never reached the reconciliation it was entitled to. One shared list is what stops that
// happening again the next time a format is added.
public static class ArchiveExtensions
{
    public const string Zip = ".zip";

    // Everything 7-Zip itself can open, shelled out to generically wherever BEM extracts a non-zip
    // archive: it reads the container's own format, so nothing about this list is tied to how BEM
    // extracts it, only to what BEM is willing to consider an archive worth looking at at all.
    public static readonly IReadOnlyCollection<string> External =
    [
        ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".zst"
    ];

    public static bool IsZip(string? extension) =>
        string.Equals(extension, Zip, StringComparison.OrdinalIgnoreCase);

    public static bool IsExternal(string? extension) =>
        extension is not null && External.Contains(extension, StringComparer.OrdinalIgnoreCase);

    public static bool IsArchive(string? extension) => IsZip(extension) || IsExternal(extension);
}
