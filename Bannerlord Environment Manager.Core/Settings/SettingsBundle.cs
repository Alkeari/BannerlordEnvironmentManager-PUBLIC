using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Settings;

public sealed record BundleFileCandidate(
    string RelativePath,
    long SizeBytes,
    IReadOnlyList<SecretFinding> Findings,
    string NotScannedReason = "")
{
    public bool IsFlagged => Findings.Count > 0;

    public bool WasScanned => NotScannedReason.Length == 0;

    public string Describe() => $"{RelativePath} ({NotScannedReason})";
}

public sealed record BundlePlan(
    string Root,
    IReadOnlyList<ModSettingsFolder> Folders,
    IReadOnlyList<BundleFileCandidate> Files,
    IReadOnlyList<string>? UnreadableFolders = null)
{
    // Folders the walk could not open. Nothing inside them reached Files at all, so they are not
    // skipped files: they are a part of the tree the scan never saw and cannot speak for.
    public IReadOnlyList<string> UnreadableFolders { get; init; } = UnreadableFolders ?? [];

    public IReadOnlyList<BundleFileCandidate> Flagged => [.. Files.Where(file => file.IsFlagged)];

    // What the scan never read. The files it opened and gave up on, and the folders it could not open
    // at all, which is the one thing a "nothing looks like a credential" line must not be allowed to
    // cover. Both travel together because to the reader they mean the same thing: nothing looked.
    public IReadOnlyList<BundleFileCandidate> NotScanned =>
    [
        .. Files.Where(file => !file.WasScanned),
        .. UnreadableFolders.Select(folder => new BundleFileCandidate(
            folder,
            0,
            [],
            Strings.Current["Core.Settings.Bundle.UnreadableFolderReason"]))
    ];
}

public sealed record BundleExportResult(
    string BundlePath,
    IReadOnlyList<string> Included,
    IReadOnlyList<string> ExcludedForSecrets,
    long SizeBytes,
    IReadOnlyList<string>? NotScanned = null)
{
    // Written into the bundle and never checked, so the bundle cannot be called clean.
    public IReadOnlyList<string> NotScanned { get; init; } = NotScanned ?? [];
}

public sealed record ImportEntry(
    string RelativePath,
    long SizeBytes,
    bool Overwrites,
    long ExistingSizeBytes,
    IReadOnlyList<SecretFinding> Findings,
    string NotScannedReason = "")
{
    public bool IsFlagged => Findings.Count > 0;

    public bool WasScanned => NotScannedReason.Length == 0;

    public string Describe() => $"{RelativePath} ({NotScannedReason})";
}

public sealed record ImportPreview(string BundlePath, IReadOnlyList<ImportEntry> Entries)
{
    public IReadOnlyList<ImportEntry> Flagged => [.. Entries.Where(entry => entry.IsFlagged)];

    public IReadOnlyList<ImportEntry> Overwriting => [.. Entries.Where(entry => entry.Overwrites)];

    public IReadOnlyList<ImportEntry> NotScanned => [.. Entries.Where(entry => !entry.WasScanned)];
}

public sealed record ImportResult(
    IReadOnlyList<string> Written,
    IReadOnlyList<string> SkippedForSecrets,
    IReadOnlyList<ArchivedSettings> BackedUp);

public sealed record BundleManifest(
    DateTimeOffset CreatedUtc,
    IReadOnlyList<string> Folders,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> ExcludedForSecrets,
    IReadOnlyList<string>? NotScanned = null)
{
    // The bundle carries the list of files in it that were never checked, so whoever opens it on
    // another machine is told the same thing the machine that built it was told.
    public IReadOnlyList<string> NotScanned { get; init; } = NotScanned ?? [];
}

// A shared bundle leaves the machine it was built on, so the default on both sides is to leave a
// flagged file out. Including one is always something the user asked for by name.
public static class SettingsBundle
{
    public const string ManifestEntryName = "bundle.json";

    public const string FilesPrefix = "files/";

    // BEM's own Nexus credentials are stored under local application data, nowhere near the mod
    // settings root this walks. This is the second line: a bundle is a file that leaves the machine,
    // and a credential must not be able to travel in one whatever folder it turns up in. The SSO
    // connection token counts, because it resumes an approved session with no further approval.
    public static bool IsBemCredentialFile(string relativePath) =>
        Path.GetFileName(relativePath) is var name
        && (string.Equals(name, Nexus.NexusApiKeyStore.FileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, Nexus.NexusApiKeyStore.PastedKeyFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, Nexus.NexusSsoTokenStore.FileName, StringComparison.OrdinalIgnoreCase));

    private static readonly JsonSerializerOptions ManifestFormat = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static BundlePlan Plan(string root, IReadOnlyList<string> relativeFolderPaths)
    {
        var all = ModSettingsScanner.Scan(root);

        var folders = relativeFolderPaths.Count == 0
            ? all
            : [.. all.Where(folder => relativeFolderPaths.Contains(folder.RelativePath, StringComparer.OrdinalIgnoreCase))];

        var files = new List<BundleFileCandidate>();
        var unreadableFolders = new List<string>();

        foreach (var folder in folders)
        {
            foreach (var unreadable in ModSettingsScanner.FindUnreadableFolders(folder.FullPath))
            {
                var relative = Path.GetRelativePath(folder.FullPath, unreadable);

                unreadableFolders.Add(relative == "."
                    ? folder.RelativePath
                    : Path.Combine(folder.RelativePath, relative));
            }

            foreach (var file in ModSettingsScanner.EnumerateFiles(folder.FullPath))
            {
                var relativePath = Path.Combine(folder.RelativePath, Path.GetRelativePath(folder.FullPath, file));

                if (IsBemCredentialFile(relativePath))
                    continue;

                var scan = SecretScanner.Inspect(file, relativePath);

                files.Add(new BundleFileCandidate(
                    relativePath, Length(file), scan.Findings, scan.WasRead ? string.Empty : scan.Reason));
            }
        }

        return new BundlePlan(
            root,
            folders,
            [.. files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)],
            [.. unreadableFolders.OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase)]);
    }

    public static BundleExportResult Export(BundlePlan plan, IReadOnlyList<string> approvedRelativePaths, string destinationPath)
    {
        var approved = new HashSet<string>(approvedRelativePaths, StringComparer.OrdinalIgnoreCase);
        var included = new List<string>();
        var excluded = new List<string>();

        var directory = Path.GetDirectoryName(destinationPath);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(destinationPath))
            File.Delete(destinationPath);

        using (var stream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var file in plan.Files)
            {
                if (file.IsFlagged && !approved.Contains(file.RelativePath))
                {
                    excluded.Add(file.RelativePath);
                    continue;
                }

                var source = Path.Combine(plan.Root, file.RelativePath);

                if (!File.Exists(source))
                    continue;

                var entry = zip.CreateEntry(FilesPrefix + ToEntryPath(file.RelativePath), CompressionLevel.Optimal);

                using var entryStream = entry.Open();
                using var sourceStream = File.OpenRead(source);
                sourceStream.CopyTo(entryStream);

                included.Add(file.RelativePath);
            }

            var manifest = new BundleManifest(
                DateTimeOffset.UtcNow,
                [.. plan.Folders.Select(folder => folder.RelativePath)],
                included,
                excluded,
                NotScannedIn(plan, included));

            var manifestEntry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);

            using var manifestStream = manifestEntry.Open();
            manifestStream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, ManifestFormat)));
        }

        return new BundleExportResult(
            destinationPath, included, excluded, Length(destinationPath), NotScannedIn(plan, included));
    }

    private static IReadOnlyList<string> NotScannedIn(BundlePlan plan, IReadOnlyList<string> included)
    {
        var wrote = new HashSet<string>(included, StringComparer.OrdinalIgnoreCase);

        return [.. plan.NotScanned.Where(file => wrote.Contains(file.RelativePath)).Select(file => file.Describe())];
    }

    public static ImportPreview Preview(string bundlePath, string root)
    {
        var entries = new List<ImportEntry>();

        using var zip = ZipFile.OpenRead(bundlePath);

        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(FilesPrefix, StringComparison.OrdinalIgnoreCase) || entry.Name.Length == 0)
                continue;

            var relativePath = ToRelativePath(entry.FullName[FilesPrefix.Length..]);

            if (relativePath is null || IsBemCredentialFile(relativePath))
                continue;

            var destination = Path.Combine(root, relativePath);
            var exists = File.Exists(destination);

            var scan = ReadEntry(entry) is { } text
                ? SecretScanner.InspectContent(relativePath, text)
                : new SecretScan([], SecretScanOutcome.Unreadable, Strings.Current["Core.Settings.Bundle.UnreadableEntryReason"]);

            entries.Add(new ImportEntry(
                relativePath,
                entry.Length,
                exists,
                exists ? Length(destination) : 0,
                scan.Findings,
                scan.WasRead ? string.Empty : scan.Reason));
        }

        return new ImportPreview(bundlePath, [.. entries.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)]);
    }

    public static ImportResult Import(
        ImportPreview preview,
        string root,
        ModSettingsArchive archive,
        IReadOnlyList<string> approvedRelativePaths)
    {
        var approved = new HashSet<string>(approvedRelativePaths, StringComparer.OrdinalIgnoreCase);
        var accepted = preview.Entries.Where(entry => !entry.IsFlagged || approved.Contains(entry.RelativePath)).ToList();
        var skipped = preview.Entries.Where(entry => entry.IsFlagged && !approved.Contains(entry.RelativePath)).ToList();

        var backedUp = new List<ArchivedSettings>();

        foreach (var owning in accepted
                     .Where(entry => entry.Overwrites)
                     .Select(entry => OwningFolder(entry.RelativePath))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ModSettingsScanner.Find(root, owning) is { } folder)
                backedUp.Add(archive.Backup(folder, ArchiveReason.ReplacedByImport));
        }

        var written = new List<string>();

        using (var zip = ZipFile.OpenRead(preview.BundlePath))
        {
            foreach (var entry in accepted)
            {
                var source = zip.GetEntry(FilesPrefix + ToEntryPath(entry.RelativePath));

                if (source is null)
                    continue;

                var destination = Path.Combine(root, entry.RelativePath);

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                source.ExtractToFile(destination, overwrite: true);
                written.Add(entry.RelativePath);
            }
        }

        return new ImportResult(written, [.. skipped.Select(entry => entry.RelativePath)], backedUp);
    }

    // MCM keeps every global-scope mod under one Global folder, so the unit that gets backed up before
    // an import is Global\<mod>, never Global itself.
    public static string OwningFolder(string relativePath)
    {
        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
            return string.Empty;

        var depth = segments.Length > 1 &&
            string.Equals(segments[0], ModSettingsScanner.GlobalFolderName, StringComparison.OrdinalIgnoreCase)
            ? 2
            : 1;

        return string.Join(Path.DirectorySeparatorChar, segments.Take(Math.Min(depth, segments.Length)));
    }

    private static string ToEntryPath(string relativePath) =>
        relativePath.Replace('\\', '/');

    // A bundle is a file from someone else, so an entry that climbs out of the settings root is
    // refused rather than followed.
    private static string? ToRelativePath(string entryPath)
    {
        var segments = entryPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0 || segments.Any(segment => segment is "." or "..") || Path.IsPathRooted(entryPath))
            return null;

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    // Null rather than empty, because an entry that could not be read and an entry with nothing in it
    // are opposite answers to whether it was checked for a credential.
    private static string? ReadEntry(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static long Length(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
