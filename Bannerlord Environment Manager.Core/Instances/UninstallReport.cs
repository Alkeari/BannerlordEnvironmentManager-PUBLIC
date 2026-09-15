using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Instances;

public sealed record UninstallItem(string Path, long Bytes, string Explanation);

public sealed record UninstallSummary(IReadOnlyList<UninstallItem> Items, IReadOnlyList<string> LiveJunctions)
{
    public long TotalBytes => Items.Sum(i => i.Bytes);

    public bool SafeToUninstall => LiveJunctions.Count == 0;
}

// The answer to "what happens to my files if I remove BEM", produced from the filesystem rather than
// from a list of things BEM believes it created. Nothing here deletes anything: the caller shows the
// list and the user decides.
public sealed class UninstallReport(CanonicalPathSet paths, InstanceRegistry registry)
{
    public UninstallSummary Build()
    {
        var items = new List<UninstallItem>();

        // The folder each instance was read from, not one re-derived from its display name: a renamed
        // instance would otherwise be reported at a path that does not exist, at zero bytes, while the
        // folder actually holding its saves went unnamed.
        foreach (var installed in registry.ReadInstalled())
        {
            items.Add(new UninstallItem(
                installed.Folder,
                SizeOf(installed.Folder),
                Strings.Current.Format("Core.Instances.Uninstall.InstanceExplanation", InstanceLabel.NameOf(installed.Record))));
        }

        AddIfPresent(items, InstanceLayout.ArchiveCacheFolder(registry.GamesRoot),
            Strings.Current["Core.Instances.Uninstall.ArchiveCacheExplanation"]);
        AddIfPresent(items, InstanceLayout.ToolsFolder(registry.GamesRoot),
            Strings.Current["Core.Instances.Uninstall.ToolsExplanation"]);

        return new UninstallSummary(
            items,
            [.. paths.Pairs.Where(p => JunctionManager.IsJunction(p.LivePath)).Select(p => p.LivePath)]);
    }

    private static void AddIfPresent(List<UninstallItem> items, string folder, string explanation)
    {
        if (Directory.Exists(folder))
            items.Add(new UninstallItem(folder, SizeOf(folder), explanation));
    }

    // Public so the Versions page can show the same size on disk it reports here, without a second
    // walk of the filesystem implemented a second time.
    public static long SizeOf(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
