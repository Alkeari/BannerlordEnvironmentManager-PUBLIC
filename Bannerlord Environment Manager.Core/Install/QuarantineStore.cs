using System.Text.Json;
using BannerlordEnvironmentManager.Core.Io;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Install;

public sealed record QuarantinedItem(
    string Id,
    string Group,
    string RelativePath,
    string OriginPath,
    string StoredPath,
    long SizeBytes,
    int FileCount,
    DateTimeOffset QuarantinedUtc,
    string Reason);

public sealed class QuarantineStore
{
    public const string IndexFileName = "index.json";

    private const int MaxCollisionSuffix = 999;

    private static readonly JsonSerializerOptions IndexFormat = new() { WriteIndented = true };

    public QuarantineStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = rootPath;
    }

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        "quarantine");

    public string RootPath { get; }

    public string IndexPath => Path.Combine(RootPath, IndexFileName);

    public IReadOnlyList<QuarantinedItem> Read()
    {
        if (!File.Exists(IndexPath))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<QuarantinedItem>>(File.ReadAllText(IndexPath), IndexFormat) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public QuarantinedItem Store(string sourceDirectory, string group, string relativePath, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        WorkshopContent.Refuse(sourceDirectory);

        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"'{sourceDirectory}' does not exist.");

        var origin = Path.GetFullPath(sourceDirectory);
        var destination = Destination(group, relativePath);

        if (DirectoryMover.TryRename(origin, destination))
            return Record(origin, group, relativePath, destination, reason);

        DirectoryMover.CopyForMove(origin, destination);

        // Listed before the original is taken away. A delete that fails partway would otherwise leave a
        // complete copy sitting in the quarantine that nothing knows about and nobody can restore or
        // discard, which is a backup only in the sense that the bytes are still somewhere.
        var item = Record(origin, group, relativePath, destination, reason);

        DirectoryMover.RemoveCopied(origin, destination);

        return item;
    }

    // For an overwrite in place, where the folder has to stay where it is: the copy goes into the
    // quarantine and the original is left alone. It is listed, restored and discarded by the same code
    // a moved one is, so an install that writes over a folder is as recoverable as one that replaces it.
    public QuarantinedItem Copy(string sourceDirectory, string group, string relativePath, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        WorkshopContent.Refuse(sourceDirectory);

        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"'{sourceDirectory}' does not exist.");

        var origin = Path.GetFullPath(sourceDirectory);
        var destination = Destination(group, relativePath);

        try
        {
            DirectoryMover.Copy(origin, destination);
        }
        catch
        {
            // A half-copied backup is worse than none: it would be listed as the copy of the folder and
            // restoring it would put back part of one.
            TryDeleteTree(destination);
            throw;
        }

        return Record(origin, group, relativePath, destination, reason);
    }

    private string Destination(string group, string relativePath) =>
        FreePath(Path.Combine(RootPath, SafeSegment(group), NormalizeRelative(relativePath)));

    private QuarantinedItem Record(
        string origin, string group, string relativePath, string destination, string reason)
    {
        var (sizeBytes, fileCount) = Measure(destination);

        var item = new QuarantinedItem(
            Guid.NewGuid().ToString("N"),
            group,
            relativePath,
            origin,
            destination,
            sizeBytes,
            fileCount,
            DateTimeOffset.UtcNow,
            reason);

        Write([.. Read(), item]);

        return item;
    }

    // A restore that overwrote whatever is at the origin would destroy the newer copy, which is the one
    // the user is more likely to want. Refusing keeps both and leaves the decision with them.
    public bool Restore(string id)
    {
        var items = Read();
        var item = items.FirstOrDefault(entry => entry.Id == id);

        // An index written before the Workshop guard existed can still name an origin under Steam's
        // folders. Putting one back there is as much BEM's business as taking it was.
        if (item is null || !Directory.Exists(item.StoredPath) || Directory.Exists(item.OriginPath)
            || WorkshopContent.Owns(item.OriginPath))
        {
            return false;
        }

        MoveDirectory(item.StoredPath, item.OriginPath);
        Write([.. items.Where(entry => entry.Id != id)]);

        return true;
    }

    // The one deliberate deletion in this store. It refuses anything the store does not itself hold, so
    // a hand-edited or stale index cannot turn a prune into a delete somewhere in the game folder.
    public bool Discard(string id)
    {
        var items = Read();
        var item = items.FirstOrDefault(entry => entry.Id == id);

        if (item is null || !IsInsideRoot(item.StoredPath))
            return false;

        DeleteTree(item.StoredPath);
        Write([.. items.Where(entry => entry.Id != id)]);

        return true;
    }

    public static (long SizeBytes, int FileCount) Measure(string directory)
    {
        long sizeBytes = 0;
        var fileCount = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    sizeBytes += new FileInfo(file).Length;
                    fileCount++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return (sizeBytes, fileCount);
    }

    // Written beside the real index and moved over it, because this index is the only thing that can
    // find the folders the store is holding. A write that stopped halfway through the file would leave
    // it unreadable, and an unreadable index is every quarantined folder unlistable at once.
    private void Write(IReadOnlyList<QuarantinedItem> items)
    {
        Directory.CreateDirectory(RootPath);

        var pending = IndexPath + ".tmp";

        File.WriteAllText(pending, JsonSerializer.Serialize(items, IndexFormat));
        File.Move(pending, IndexPath, overwrite: true);
    }

    private static void MoveDirectory(string source, string destination) =>
        DirectoryMover.Move(source, destination);

    internal static void DeleteTree(string path) => DirectoryMover.DeleteTree(path);

    internal static void TryDeleteTree(string path) => DirectoryMover.TryDeleteTree(path);

    private bool IsInsideRoot(string path)
    {
        var root = Path.GetFullPath(RootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string FreePath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
            return path;

        for (var suffix = 2; suffix <= MaxCollisionSuffix; suffix++)
        {
            var candidate = $"{path} ({suffix})";

            if (!Directory.Exists(candidate) && !File.Exists(candidate))
                return candidate;
        }

        throw new IOException($"'{path}' already holds {MaxCollisionSuffix} quarantined copies.");
    }

    private static string SafeSegment(string value)
    {
        var name = Path.GetFileName(value.Trim().TrimEnd('/', '\\'));

        return string.IsNullOrWhiteSpace(name) || name is "." or ".." ? "unknown" : name;
    }

    private static string NormalizeRelative(string relativePath)
    {
        var trimmed = (relativePath ?? string.Empty).Trim().Trim('/', '\\');

        if (trimmed.Length == 0 || trimmed.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(trimmed))
            throw new ArgumentException($"'{relativePath}' is not a relative path inside the module.", nameof(relativePath));

        return trimmed.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }
}
