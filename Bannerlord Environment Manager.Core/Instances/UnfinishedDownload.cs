namespace BannerlordEnvironmentManager.Core.Instances;

// A folder in the games root that holds part of a download and no instance. A download that stops
// short leaves exactly this: bytes on the disk, no instance.json, and nothing anywhere in BEM that
// mentions it. The user is owed a way forward from that folder rather than a folder they have to
// find in Explorer to understand.
public sealed record UnfinishedDownload(string Folder, string Name, long Bytes);

public static class UnfinishedDownloadScan
{
    public static IReadOnlyList<UnfinishedDownload> In(string gamesRoot) => In(gamesRoot, UninstallReport.SizeOf);

    // sizeOf is a parameter so the decision - which folders count - can be tested without walking
    // tens of gigabytes.
    public static IReadOnlyList<UnfinishedDownload> In(string gamesRoot, Func<string, long> sizeOf)
    {
        ArgumentNullException.ThrowIfNull(sizeOf);

        if (string.IsNullOrWhiteSpace(gamesRoot) || !Directory.Exists(gamesRoot))
            return [];

        var found = new List<UnfinishedDownload>();

        try
        {
            foreach (var folder in Directory.EnumerateDirectories(gamesRoot))
            {
                if (!IsUnfinished(folder))
                    continue;

                found.Add(new UnfinishedDownload(folder, Path.GetFileName(folder), sizeOf(folder)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
        }

        return [.. found.OrderBy(download => download.Name, StringComparer.OrdinalIgnoreCase)];
    }

    // Three things at once, and all three are needed. A dot folder is BEM's own (.tools, .archives).
    // A folder holding an instance.json is an instance, however small. A folder with no Game folder
    // under it is not something a download made, and the games root is a folder on the user's own
    // drive: whatever else is sitting in there is theirs and is never offered up for removal.
    public static bool IsUnfinished(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return false;

        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return !name.StartsWith('.')
            && !File.Exists(InstanceLayout.MetadataPath(folder))
            && Directory.Exists(InstanceLayout.GameFolder(folder));
    }
}
