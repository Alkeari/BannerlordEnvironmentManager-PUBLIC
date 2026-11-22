using System.Globalization;
using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public sealed record ClearCandidate(string Path, long SizeBytes);

public sealed record ClearScope(int FileCount, long SizeBytes, int FolderCount)
{
    // The noun arrives as a key, not as a word, because the sentence counts two different things: the
    // frame pluralizes on the folder count while the noun has to agree with the file count. Selecting
    // it here on FileCount is what lets the two numbers disagree without the noun going wrong, and in a
    // language that declines a noun after a numeral it is the only way the noun can inflect at all.
    public string Describe(string nounKey) => Strings.Current.Plural(
        "Core.Diagnostics.ClearedFile.Scope",
        FolderCount,
        FileCount,
        Strings.Current.Plural(nounKey, FileCount),
        ClearedFileStore.DescribeSize(SizeBytes));
}

public sealed record ClearedFile(string OriginPath, string StoredPath, long SizeBytes);

public sealed record ClearRefusal(string OriginPath, string Reason);

public sealed record ClearedBatch(DateTimeOffset Cleared, string Reason, IReadOnlyList<ClearedFile> Files);

public sealed record ClearResult(
    string BatchPath,
    IReadOnlyList<ClearedFile> Moved,
    IReadOnlyList<ClearRefusal> Skipped,
    // Why the batch index could not be written, empty when it was. The files are still in the batch
    // folder, but Restore reads the index to know what to offer, so without one the way back is by
    // hand and the user has to be told that rather than shown a clean success.
    string IndexError = "")
{
    public bool IsRestorable => Moved.Count == 0 || IndexError.Length == 0;

    // Whatever the caller asked for, this reports only what reached the batch folder. A message that
    // says 69 files were cleared when the game still had 3 of them open sends the user looking for
    // files that are exactly where they were.
    public string Describe()
    {
        if (Moved.Count == 0 && Skipped.Count == 0)
            return Strings.Current["Core.Diagnostics.ClearedFile.NothingMovedNoneSelected"];

        var moved = Moved.Count == 0
            ? Strings.Current["Core.Diagnostics.ClearedFile.MovedNone"]
            : Strings.Current.Plural(
                "Core.Diagnostics.ClearedFile.Moved",
                Moved.Count,
                ClearedFileStore.DescribeSize(Moved.Sum(f => f.SizeBytes)),
                BatchPath);

        if (Skipped.Count > 0)
        {
            var left = string.Join("; ", Skipped.Select(s => $"{System.IO.Path.GetFileName(s.OriginPath)}: {s.Reason}"));

            moved = moved + Strings.Current.Plural("Core.Diagnostics.ClearedFile.SkippedSuffix", Skipped.Count, left);
        }

        return IsRestorable
            ? moved
            : moved + Strings.Current.Format("Core.Diagnostics.ClearedFile.NotRestorable", BatchPath, IndexError);
    }
}

public sealed record ClearedBatchSummary(
    string BatchPath,
    DateTimeOffset Cleared,
    string Reason,
    IReadOnlyList<ClearedFile> Files)
{
    public int FileCount => Files.Count;

    public long SizeBytes => Files.Sum(file => file.SizeBytes);

    public IReadOnlyList<string> OriginFolders =>
    [
        .. Files
            .Select(file => Path.GetDirectoryName(file.OriginPath) ?? string.Empty)
            .Where(folder => folder.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
    ];

    public string Describe() => Strings.Current.Plural(
        "Core.Diagnostics.ClearedFile.BatchSummary",
        FileCount,
        Cleared.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        ClearedFileStore.DescribeSize(SizeBytes),
        Reason);

    public string DescribeOrigins() => OriginFolders.Count == 0
        ? Strings.Current["Core.Diagnostics.ClearedFile.NoOriginFolders"]
        : Strings.Current.Format("Core.Diagnostics.ClearedFile.OriginFoldersPrefix", string.Join("; ", OriginFolders));
}

public sealed record ClearedBatchList(
    IReadOnlyList<ClearedBatchSummary> Batches,
    IReadOnlyList<string> Unreadable)
{
    public string Describe()
    {
        string found;

        if (Batches.Count == 0)
        {
            found = Strings.Current["Core.Diagnostics.ClearedFile.List.None"];
        }
        else
        {
            var fileCount = Batches.Sum(batch => batch.FileCount);

            found = Strings.Current.Plural("Core.Diagnostics.ClearedFile.List.BatchCount", Batches.Count)
                + Strings.Current.Plural(
                    "Core.Diagnostics.ClearedFile.List.FileCount",
                    fileCount,
                    ClearedFileStore.DescribeSize(Batches.Sum(batch => batch.SizeBytes)));
        }

        if (Unreadable.Count == 0)
            return found;

        // A batch BEM cannot read is not a batch that is not there, and the difference decides whether
        // the user goes looking in the folder by hand.
        return found + Strings.Current.Plural(
            "Core.Diagnostics.ClearedFile.List.UnreadableSuffix", Unreadable.Count, string.Join("; ", Unreadable));
    }
}

public sealed record RestoredFile(string OriginPath, string StoredPath, string DisplacedPath, long SizeBytes);

public sealed record RestoreRefusal(string OriginPath, string Reason);

public sealed record RestoreResult(
    IReadOnlyList<RestoredFile> Restored,
    IReadOnlyList<RestoreRefusal> Skipped,
    bool BatchRemains,
    // Why the rewritten index could not be saved, empty when it was. What is left in the batch is
    // still on disk; it is the list Restore reads that is now stale.
    string IndexError = "")
{
    public string Describe()
    {
        if (Restored.Count == 0 && Skipped.Count == 0)
            return Strings.Current["Core.Diagnostics.ClearedFile.Restore.NoneSelected"];

        var parts = new List<string>
        {
            Restored.Count == 0
                ? Strings.Current["Core.Diagnostics.ClearedFile.Restore.None"]
                : Strings.Current.Plural(
                    "Core.Diagnostics.ClearedFile.Restore.Put",
                    Restored.Count,
                    ClearedFileStore.DescribeSize(Restored.Sum(f => f.SizeBytes)))
        };

        var displaced = Restored.Where(f => f.DisplacedPath.Length > 0).ToList();

        if (displaced.Count > 0)
        {
            parts.Add(Strings.Current.Plural(
                "Core.Diagnostics.ClearedFile.Restore.Displaced",
                displaced.Count,
                string.Join("; ", displaced.Select(f => Path.GetFileName(f.DisplacedPath)))));
        }

        if (Restored.Count > 0 && !BatchRemains)
            parts.Add(Strings.Current["Core.Diagnostics.ClearedFile.Restore.BatchGone"]);

        if (Skipped.Count > 0)
        {
            parts.Add(Strings.Current.Plural(
                "Core.Diagnostics.ClearedFile.Restore.Skipped",
                Skipped.Count,
                string.Join("; ", Skipped.Select(s => $"{Path.GetFileName(s.OriginPath)}: {s.Reason}"))));
        }

        if (IndexError.Length > 0)
        {
            parts.Add(Strings.Current.Format("Core.Diagnostics.ClearedFile.Restore.IndexError", IndexError));
        }

        return string.Join(" ", parts);
    }
}

// Where the files went is the caller's to say, because the caller is what chose how to remove them:
// the app hands in the Recycle Bin and a test hands in a plain delete. Claiming either here would
// state one of them wrongly.
public sealed record DiscardResult(string BatchPath, int FileCount, long SizeBytes, bool Removed, string Error)
{
    public string Describe() => Removed
        ? Strings.Current.Plural(
            "Core.Diagnostics.ClearedFile.Discard.Deleted", FileCount, ClearedFileStore.DescribeSize(SizeBytes), BatchPath)
        : Strings.Current.Format("Core.Diagnostics.ClearedFile.Discard.Failed", BatchPath, Error);
}

// Moves log and crash report files out of the way instead of deleting them. Every file lands under a
// timestamped batch folder that keeps its drive and folder structure, next to an index naming where
// each one came from, so a clear is always undoable by hand.
public sealed class ClearedFileStore
{
    public const string IndexFileName = "index.json";

    private static readonly JsonSerializerOptions IndexFormat = new() { WriteIndented = true };

    public ClearedFileStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = rootPath;
    }

    // Per version. Every batch under here is one version's log and crash files, set aside so the clear
    // stays undoable, and one machine-wide store offered a v1.4.8 batch as restorable while a freshly
    // downloaded v1.5.2 was selected, onto folders that version has never written. The resting version
    // keeps the path it has always used, so nothing already cleared moves or stops being restorable.
    public static string GetDefaultRoot(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, "cleared");

    public string RootPath { get; }

    public static ClearScope Measure(IReadOnlyList<ClearCandidate> files) => new(
        files.Count,
        files.Sum(f => f.SizeBytes),
        files.Select(f => Path.GetDirectoryName(f.Path) ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count());

    public static string DescribeSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        1 => "1 byte",
        _ => $"{bytes} bytes"
    };

    public ClearResult Clear(
        IReadOnlyList<ClearCandidate> files,
        IReadOnlyList<string> allowedRoots,
        string reason,
        DateTimeOffset stamp)
    {
        var batchPath = Path.Combine(RootPath, stamp.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture));
        var roots = allowedRoots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(Full).ToList();
        var moved = new List<ClearedFile>();
        var skipped = new List<ClearRefusal>();

        foreach (var file in files)
        {
            if (Move(file, roots, batchPath) is { } stored)
                moved.Add(stored);
            else
                skipped.Add(Refuse(file, roots));
        }

        var indexError = moved.Count > 0
            ? WriteIndex(batchPath, new ClearedBatch(stamp, reason, moved))
            : string.Empty;

        return new ClearResult(batchPath, moved, skipped, indexError);
    }

    public ClearedBatchList List()
    {
        if (!Directory.Exists(RootPath))
            return new ClearedBatchList([], []);

        List<string> folders;

        try
        {
            folders = [.. Directory.EnumerateDirectories(RootPath)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ClearedBatchList([], [$"{RootPath}: {ex.Message}"]);
        }

        var batches = new List<ClearedBatchSummary>();
        var unreadable = new List<string>();

        foreach (var folder in folders)
        {
            if (ReadBatch(folder) is { } summary)
                batches.Add(summary);
            else
                unreadable.Add(folder);
        }

        return new ClearedBatchList([.. batches.OrderByDescending(batch => batch.Cleared)], unreadable);
    }

    public ClearScope MeasureBatch(string batchPath) => Measure(StoredFiles(batchPath));

    public RestoreResult Restore(string batchPath, DateTimeOffset stamp) =>
        ReadBatch(batchPath) is { } batch
            ? Restore(batchPath, batch.Files, stamp)
            : NoIndex(batchPath);

    // Anything already sitting at the destination is renamed out of the way rather than refused or
    // overwritten. The game recreates rgl_log.txt the next time it runs, so refusing would strand every
    // restore after one launch, and asking once per file is unusable on a batch of seventy.
    public RestoreResult Restore(string batchPath, IReadOnlyList<ClearedFile> files, DateTimeOffset stamp)
    {
        if (ReadBatch(batchPath) is not { } batch)
            return NoIndex(batchPath);

        var wanted = files.Select(file => file.StoredPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var restored = new List<RestoredFile>();
        var skipped = new List<RestoreRefusal>();
        var remaining = new List<ClearedFile>();

        foreach (var file in batch.Files)
        {
            if (!wanted.Contains(file.StoredPath))
            {
                remaining.Add(file);
                continue;
            }

            if (PutBack(file, stamp) is { } done)
            {
                restored.Add(done);
                continue;
            }

            skipped.Add(RefuseRestore(file));
            remaining.Add(file);
        }

        var remains = remaining.Count > 0 || !Remove(batchPath);

        var indexError = remains
            ? WriteIndex(batchPath, new ClearedBatch(batch.Cleared, batch.Reason, remaining))
            : string.Empty;

        return new RestoreResult(restored, skipped, remains, indexError);
    }

    // The one sanctioned deletion in this store. Everything else here moves files; this one ends them,
    // so it never touches a folder outside the store's own root however it was called. How the folder
    // goes is delegated so Core stays free of Windows: the app hands in the Recycle Bin call, and a
    // caller that hands in nothing gets the plain recursive delete.
    public DiscardResult Discard(string batchPath, Action<string>? removeFolder = null)
    {
        var scope = MeasureBatch(batchPath);

        if (!InsideRoot(batchPath))
        {
            return new DiscardResult(
                batchPath, 0, 0, false, Strings.Current["Core.Diagnostics.ClearedFile.Discard.OutsideRoot"]);
        }

        if (!Directory.Exists(batchPath))
        {
            return new DiscardResult(
                batchPath, 0, 0, false, Strings.Current["Core.Diagnostics.ClearedFile.Discard.Gone"]);
        }

        try
        {
            if (removeFolder is null)
                Directory.Delete(batchPath, recursive: true);
            else
                removeFolder(batchPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DiscardResult(batchPath, scope.FileCount, scope.SizeBytes, false, ex.Message);
        }

        return new DiscardResult(batchPath, scope.FileCount, scope.SizeBytes, true, string.Empty);
    }

    private static RestoreResult NoIndex(string batchPath) => new(
        [],
        [new RestoreRefusal(batchPath, Strings.Current["Core.Diagnostics.ClearedFile.NoIndexReason"])],
        BatchRemains: true);

    private static ClearedBatchSummary? ReadBatch(string batchPath)
    {
        var indexPath = Path.Combine(batchPath, IndexFileName);

        if (!File.Exists(indexPath))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ClearedBatch>(File.ReadAllText(indexPath)) is { } batch
                ? new ClearedBatchSummary(batchPath, batch.Cleared, batch.Reason, batch.Files)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ClearCandidate> StoredFiles(string batchPath)
    {
        try
        {
            return
            [
                .. Directory.EnumerateFiles(batchPath, "*", SearchOption.AllDirectories)
                    .Where(path => !string.Equals(
                        Path.GetFileName(path), IndexFileName, StringComparison.OrdinalIgnoreCase))
                    .Select(path => new ClearCandidate(path, SizeOf(path)))
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private RestoredFile? PutBack(ClearedFile file, DateTimeOffset stamp)
    {
        if (!File.Exists(file.StoredPath))
            return null;

        var displaced = string.Empty;

        try
        {
            var folder = Path.GetDirectoryName(file.OriginPath);

            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            if (File.Exists(file.OriginPath))
            {
                displaced = Free(Displaced(file.OriginPath, stamp));
                File.Move(file.OriginPath, displaced);
            }

            File.Move(file.StoredPath, file.OriginPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PutDisplacedBack(displaced, file.OriginPath);
            return null;
        }

        return new RestoredFile(file.OriginPath, file.StoredPath, displaced, file.SizeBytes);
    }

    // A restore that failed halfway would otherwise leave the newer file under a name the user never
    // chose, for a cleared file that never arrived.
    private static void PutDisplacedBack(string displaced, string originPath)
    {
        if (displaced.Length == 0 || !File.Exists(displaced) || File.Exists(originPath))
            return;

        try
        {
            File.Move(displaced, originPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static RestoreRefusal RefuseRestore(ClearedFile file) => new(
        file.OriginPath,
        !File.Exists(file.StoredPath)
            ? Strings.Current["Core.Diagnostics.ClearedFile.RestoreRefusal.Gone"]
            : Strings.Current["Core.Diagnostics.ClearedFile.RestoreRefusal.Locked"]);

    // Only ever removes a folder that holds nothing but its own index, so an emptied batch stops being
    // offered without this becoming a second way to delete a file.
    private bool Remove(string batchPath)
    {
        if (!InsideRoot(batchPath) || StoredFiles(batchPath).Count > 0)
            return false;

        try
        {
            Directory.Delete(batchPath, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool InsideRoot(string batchPath) => Inside(batchPath, [Full(RootPath)]);

    private static string Displaced(string originPath, DateTimeOffset stamp)
    {
        var folder = Path.GetDirectoryName(originPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(originPath);
        var extension = Path.GetExtension(originPath);

        return Path.Combine(folder, $"{stem} (displaced by restore {stamp:yyyy-MM-dd HH-mm-ss}){extension}");
    }

    private static long SizeOf(string path)
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

    private static ClearRefusal Refuse(ClearCandidate file, List<string> roots) => new(
        file.Path,
        !Inside(file.Path, roots)
            ? Strings.Current["Core.Diagnostics.ClearedFile.ClearRefusal.OutsideScope"]
            : !File.Exists(file.Path)
                ? Strings.Current["Core.Diagnostics.ClearedFile.ClearRefusal.Gone"]
                : Strings.Current["Core.Diagnostics.ClearedFile.ClearRefusal.Locked"]);

    private ClearedFile? Move(ClearCandidate file, List<string> roots, string batchPath)
    {
        if (!Inside(file.Path, roots) || !File.Exists(file.Path))
            return null;

        var destination = Free(Path.Combine(batchPath, Preserved(file.Path)));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(file.Path, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return new ClearedFile(file.Path, destination, file.SizeBytes);
    }

    // Returns why the index could not be written, empty when it was. Swallowing this reported a clear
    // as done while Restore had no list to offer: the files were moved and the way back was gone, which
    // is a safety net that is unreachable from where the danger is.
    private static string WriteIndex(string batchPath, ClearedBatch batch)
    {
        try
        {
            Directory.CreateDirectory(batchPath);
            File.WriteAllText(
                Path.Combine(batchPath, IndexFileName),
                JsonSerializer.Serialize(batch, IndexFormat));

            return string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    // A path is inside a root only at a separator boundary, so a "logs backup" folder next to "logs"
    // is not swept along with it.
    private static bool Inside(string path, List<string> roots)
    {
        var full = Full(path);

        return roots.Any(root =>
            full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static string Full(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    // The drive becomes the first segment so two files with the same folder path on different drives
    // cannot land on top of each other, and so the original path is readable from the batch folder
    // alone rather than only from the index.
    private static string Preserved(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var relative = full[root.Length..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var drive = root.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':')
            .Replace(Path.DirectorySeparatorChar, '_')
            .Replace(Path.AltDirectorySeparatorChar, '_');

        return string.IsNullOrWhiteSpace(drive) ? relative : Path.Combine(drive, relative);
    }

    private static string Free(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;

        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var suffix = 2; ; suffix++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({suffix}){extension}");

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }
}
