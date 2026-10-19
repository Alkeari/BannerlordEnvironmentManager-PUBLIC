namespace BannerlordEnvironmentManager.Core.Instances;

// Whether a folder holds the whole version, measured against what a finished install of it weighs.
// DepotDownloader can exit quietly having transferred nothing, so its exit code alone is not proof a
// download arrived. The figure to compare against is SteamDepotCatalog.InstalledSizeBytes and never
// the sum of the depots: overlapping depots publish shared files twice, and comparing a real install
// against that sum rejected a complete v1.5.2 on every check.
public static class InstanceCompleteness
{
    // A percent of slack, because the tool's own manifest cache and Steam's published figure land
    // either side of each other.
    private const long PermittedShortfallPercent = 1;

    public static bool Holds(long bytesOnDisk, long publishedBytes) =>
        publishedBytes > 0 && bytesOnDisk >= publishedBytes / 100 * (100 - PermittedShortfallPercent);

    public static string Shortfall(long bytesOnDisk, long publishedBytes) =>
        $"{Gigabytes(bytesOnDisk)} of {Gigabytes(publishedBytes)}";

    private static string Gigabytes(long bytes) => $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
}
