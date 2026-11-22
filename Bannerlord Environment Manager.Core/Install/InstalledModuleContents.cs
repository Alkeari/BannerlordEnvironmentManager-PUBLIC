using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BannerlordEnvironmentManager.Core.Install;

public enum ModuleContentComparison
{
    // Nothing was recorded when this module went in, so there is nothing to compare against. Not the
    // same statement as "it has not changed".
    NotRecorded,
    Unreadable,
    Same,
    Changed,
    // Asked without reading the module's files, and nothing already held answers for the folder as it
    // stands now. Not the same statement as Same, and not the same statement as NotRecorded: a
    // fingerprint was recorded, and the caller declined to pay a read of every file in the folder to
    // compare against it.
    NotRead
}

// A digest of a module's files as they sit on disk. It is deterministic across machines: the same
// module folder anywhere produces the same string, because nothing machine-specific goes into it.
public sealed record ModuleContentFingerprint(string Hash, int FileCount, long TotalBytes)
{
    public override string ToString() =>
        $"{Hash}:{FileCount.ToString(CultureInfo.InvariantCulture)}:{TotalBytes.ToString(CultureInfo.InvariantCulture)}";

    public static ModuleContentFingerprint? Parse(string? text)
    {
        var parts = text?.Split(':');

        return parts is { Length: 3 }
            && parts[0].Length > 0
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var files)
            && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)
                ? new ModuleContentFingerprint(parts[0], files, bytes)
                : null;
    }
}

// What BEM can compare without asking anybody anything: the files it wrote against the files that are
// there now. This says whether the copy on disk is still the copy that was installed, and nothing
// whatever about what Nexus holds - Nexus publishes no hash of an extracted module, only of the
// archive it came in. Those are different questions and are kept apart deliberately.
public static class InstalledModuleContents
{
    // BEM rewrites this itself when it repairs a manifest, so counting it would report BEM's own edits
    // as the mod having changed. The version it declares is recorded separately.
    public const string ManifestFileName = "SubModule.xml";

    private const string BackupSuffix = ".bem.bak";

    public static ModuleContentFingerprint? Read(string moduleFolderPath)
    {
        var cursor = System.Diagnostics.Stopwatch.StartNew();
        var result = ReadCore(moduleFolderPath);
        cursor.Stop();
        if (cursor.ElapsedMilliseconds >= 20)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bem_perf.log"),
                    $"[P] {DateTime.Now:HH:mm:ss.fff} Contents.Read elapsed={cursor.ElapsedMilliseconds}ms thread={(Environment.CurrentManagedThreadId != 1 ? "W" : "UI")} {moduleFolderPath}\r\n");
            }
            catch
            {
            }
        }
        return result;
    }

    private static ModuleContentFingerprint? ReadCore(string moduleFolderPath)
    {
        if (string.IsNullOrWhiteSpace(moduleFolderPath) || !Directory.Exists(moduleFolderPath))
            return null;

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(moduleFolderPath));

            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => Counted(root, path))
                .OrderBy(path => Relative(root, path), StringComparer.Ordinal)
                .ToList();

            var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var count = 0;
            long total = 0;

            foreach (var path in files)
            {
                var length = new FileInfo(path).Length;

                digest.AppendData(Encoding.UTF8.GetBytes($"{Relative(root, path)}\n{length}\n"));

                using (var stream = File.OpenRead(path))
                    digest.AppendData(SHA256.HashData(stream));

                count++;
                total += length;
            }

            return new ModuleContentFingerprint(Convert.ToHexStringLower(digest.GetHashAndReset()), count, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public static ModuleContentComparison Compare(string moduleFolderPath, string? recorded)
    {
        if (ModuleContentFingerprint.Parse(recorded) is not { } was)
            return ModuleContentComparison.NotRecorded;

        var now = Cache.Recall(moduleFolderPath, () => Read(moduleFolderPath)?.ToString());

        return now is not { } current
            ? ModuleContentComparison.Unreadable
            : current == was.ToString()
                ? ModuleContentComparison.Same
                : ModuleContentComparison.Changed;
    }

    // The same comparison for a caller that must not disturb the folder. Compare opens every file in
    // the module in turn and holds each one for the length of its hash, measured on a real install at
    // between a fifth of a second and six seconds for one folder; Windows refuses to rename a folder
    // while any file inside it is open, so for all of that time a mod build deploying into that folder
    // is refused. A refresh nobody asked for has no business costing somebody else's build, so it
    // answers from what is already held and says NotRead when nothing is.
    public static ModuleContentComparison CompareWithoutReading(string moduleFolderPath, string? recorded)
    {
        if (ModuleContentFingerprint.Parse(recorded) is not { } was)
            return ModuleContentComparison.NotRecorded;

        return Cache.RecallHeld(moduleFolderPath) is not { } current
            ? ModuleContentComparison.NotRead
            : current == was.ToString()
                ? ModuleContentComparison.Same
                : ModuleContentComparison.Changed;
    }

    // Reused by ModuleFingerprintCache for its metadata signature, so the two never disagree about
    // which files count.
    internal static bool Counted(string root, string path)
    {
        var relative = Relative(root, path);

        return !relative.EndsWith(BackupSuffix, StringComparison.Ordinal)
               && !relative.Equals(ManifestFileName.ToLowerInvariant(), StringComparison.Ordinal);
    }

    internal static string RelativeForSignature(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static readonly ModuleFingerprintCache Cache = new(ModuleFingerprintCache.DefaultPath);

    // Case-insensitively and with forward slashes, so the same module folder copied between a Steam
    // install and a GOG one still fingerprints the same.
    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/').ToLowerInvariant();
}
