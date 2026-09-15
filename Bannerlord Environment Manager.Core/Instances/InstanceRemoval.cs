using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Instances;

// One thing to delete and whether it is a folder, so the caller sends it to the Recycle Bin with the
// right call rather than deciding for itself what a path is.
public sealed record InstanceRemovalItem(string Path, bool IsFolder);

// What removing a version would actually delete. A complete removal names the instance folder itself
// rather than a list of the subfolders BEM believes it created, which is what leaves a version's
// saves, configs, shader cache and logs behind after an uninstall.
public sealed record InstanceRemovalPlan(
    string InstanceFolder,
    bool IsComplete,
    IReadOnlyList<InstanceRemovalItem> Items,
    string? ReferencedGameFolderKept)
{
    // A complete removal that leaves the folder standing is a result to report, not a success to
    // claim, so the caller checks this after carrying the plan out.
    public bool InstanceFolderRemains => Directory.Exists(InstanceFolder);

    public bool Reaches(string path) => Items.Any(item => InstanceRemoval.Covers(item.Path, path));
}

// How much a complete removal would take with it, counted from disk rather than estimated, and the
// sentence that says so.
public sealed record InstanceRemovalSize(string Folder, int Items, long Bytes)
{
    public string Describe() =>
        Strings.Current.Plural("Core.Instances.Removal.CompleteContents", Items, Folder, FormatBytes(Bytes));

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB"
    };
}

// Why a removal was refused. Resting is a decision the user reverses by making another instance
// resting; the other two are a version that is in use right now, which a removal would delete out
// from under a running game.
public enum RemovalBlock
{
    None,
    IsResting,
    InPlay,
    GameRunning
}

// The decision behind the Remove button: whether this instance may go at all, what a removal would
// delete, and how much that is. Nothing here deletes anything.
public static class InstanceRemoval
{
    // The one gate the Remove button asks before it deletes anything. Order is by what the user can
    // do about it: making another instance resting is a decision, while a live junction or a running
    // game is a wait.
    public static RemovalBlock Check(
        InstalledInstance instance,
        string? restingInstanceId,
        bool gameRunning,
        IEnumerable<string> liveJunctionTargets)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(liveJunctionTargets);

        if (!IsRemovable(instance, restingInstanceId))
            return RemovalBlock.IsResting;

        // A launch on a version that is not the resting one points the machine's canonical folders
        // at that version's own store folders for the whole run. Deleting the folder those
        // junctions resolve into is deleting the campaign the game is writing, so this is checked
        // against where the junctions actually point rather than against which id is recorded as
        // active: a teardown that has not finished is the same danger with nothing running.
        if (liveJunctionTargets.Any(target => Covers(instance.Folder, target)))
            return RemovalBlock.InPlay;

        // The process list cannot say which version a running game belongs to, so any Bannerlord
        // process refuses every removal, exactly as changing the resting instance and switching
        // versions already do.
        if (gameRunning)
            return RemovalBlock.GameRunning;

        return RemovalBlock.None;
    }

    // Where each canonical folder that is currently a junction resolves to. The instance those
    // targets sit under is the one being played, whatever the settings file records as active.
    public static IReadOnlyList<string> LiveJunctionTargets(CanonicalPathSet paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return [.. paths.Pairs.Select(pair => JunctionManager.TargetOf(pair.LivePath)).OfType<string>()];
    }

    // The resting instance is the one the machine's canonical folders point at, so removing it would
    // take the game out from under those junctions.
    public static bool IsRemovable(InstalledInstance instance, string? restingInstanceId)
    {
        ArgumentNullException.ThrowIfNull(instance);

        return !string.Equals(instance.Record.Id, restingInstanceId, StringComparison.Ordinal);
    }

    // stateRoots names where BEM's own per-version state is filed, and is null everywhere but a test:
    // the plan reaches real folders, and a test that could not say which roots it meant would be
    // reading the machine's own.
    public static InstanceRemovalPlan Plan(InstalledInstance instance, bool complete, IEnumerable<string>? stateRoots = null)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var folder = instance.Folder;

        // A referenced instance's Game folder belongs to somebody else, in practice Steam, and no
        // removal may reach it. It normally sits outside the instance folder entirely; excluding it
        // by path is what holds if a games root was ever set somewhere that puts it inside.
        var referenced = instance.Record.GameFolderIsReferenced ? instance.Record.GameFolder : null;

        if (!complete)
            return new InstanceRemovalPlan(folder, false, [.. Existing(DefaultItems(folder, referenced))], referenced);

        var items = new List<InstanceRemovalItem>();

        if (referenced is not null && Covers(folder, referenced))
        {
            items.AddRange(TopLevel(folder)
                .Where(item => !Covers(item.Path, referenced) && !Covers(referenced, item.Path)));
        }
        else if (Directory.Exists(folder))
        {
            items.Add(new InstanceRemovalItem(folder, true));
        }

        // A complete removal means nothing of this version stays, and BEM's own per-version state is
        // part of the version: the launch settings, the accepted findings, the dividers, the launch
        // history and the saved profiles. It lives in AppData rather than in the instance folder, so
        // a plan built from the folder alone never reached it, and it is filed under the folder's
        // name, so the next install to take that name inherited a stranger's. It goes to the Recycle
        // Bin with everything else, which is what keeps a removal recoverable.
        items.AddRange(InstanceStateFolder.FoldersFor(stateRoots ?? InstanceStateFolder.StateRoots(), folder)
            .Where(Directory.Exists)
            .Select(path => new InstanceRemovalItem(path, true)));

        return new InstanceRemovalPlan(folder, true, items, referenced);
    }

    public static InstanceRemovalSize Measure(string instanceFolder)
    {
        try
        {
            if (!Directory.Exists(instanceFolder))
                return new InstanceRemovalSize(instanceFolder, 0, 0);

            var items = Directory.EnumerateFileSystemEntries(instanceFolder, "*", SearchOption.AllDirectories).Count();

            return new InstanceRemovalSize(instanceFolder, items, UninstallReport.SizeOf(instanceFolder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new InstanceRemovalSize(instanceFolder, 0, 0);
        }
    }

    // True when deleting container takes path with it: the same path, or one below it.
    internal static bool Covers(string container, string path)
    {
        var above = Normalize(container);
        var below = Normalize(path);

        return string.Equals(above, below, StringComparison.OrdinalIgnoreCase)
            || below.StartsWith(above + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<InstanceRemovalItem> DefaultItems(string folder, string? referenced)
    {
        if (referenced is null)
            yield return new InstanceRemovalItem(InstanceLayout.GameFolder(folder), true);

        yield return new InstanceRemovalItem(InstanceLayout.MetadataPath(folder), false);
    }

    private static IEnumerable<InstanceRemovalItem> Existing(IEnumerable<InstanceRemovalItem> items) =>
        items.Where(item => item.IsFolder ? Directory.Exists(item.Path) : File.Exists(item.Path));

    private static IEnumerable<InstanceRemovalItem> TopLevel(string folder)
    {
        if (!Directory.Exists(folder))
            return [];

        return Directory.EnumerateDirectories(folder).Select(p => new InstanceRemovalItem(p, true))
            .Concat(Directory.EnumerateFiles(folder).Select(p => new InstanceRemovalItem(p, false)));
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
