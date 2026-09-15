using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.DryRun;

// NotProtected is the half that used to have nowhere to go. A file the copy could not take is a file
// the run was not protected against, and saying nothing about it is what let the interface promise a
// safety net that did not exist.
public sealed record LaunchConfigRestoreResult(
    int Restored,
    int MovedAside,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<string>? NotProtected = null,
    IReadOnlyList<string>? RestoredPaths = null,
    IReadOnlyList<string>? NotRestoredPaths = null,
    IReadOnlyList<string>? RemovedPaths = null,
    string RemovedTo = "",
    IReadOnlyList<string>? NotRemovedPaths = null)
{
    public IReadOnlyList<string> NotProtected { get; init; } = NotProtected ?? [];

    public IReadOnlyList<string> RestoredPaths { get; init; } = RestoredPaths ?? [];

    public IReadOnlyList<string> NotRestoredPaths { get; init; } = NotRestoredPaths ?? [];

    public IReadOnlyList<string> RemovedPaths { get; init; } = RemovedPaths ?? [];

    // A file the run added that would not move out is the fourth outcome, and it used to land in no
    // list at all. That made a run that left one of its own files in Configs report that it had left
    // nothing changed, which is the safety net claiming a clean bill it had not earned.
    public IReadOnlyList<string> NotRemovedPaths { get; init; } = NotRemovedPaths ?? [];

    public bool IsClean => NotProtected.Count == 0 && ChangedPaths.Count == 0;

    // Three outcomes that were being read out as one. A file that would not restore and a file taken
    // out of Configs are the opposite of "put back", and the clean sentence is withheld entirely
    // while there is a file BEM could not copy, because for that one it does not know.
    public IReadOnlyList<string> Describe()
    {
        if (IsClean)
            return [Strings.Current["Core.DryRun.ConfigSnapshot.Clean"]];

        var lines = new List<string>();

        if (NotProtected.Count > 0)
        {
            lines.Add(Strings.Current.Plural(
                "Core.DryRun.ConfigSnapshot.NotProtected", NotProtected.Count, Join(NotProtected)));
        }

        if (RestoredPaths.Count > 0)
        {
            lines.Add(Strings.Current.Plural(
                "Core.DryRun.ConfigSnapshot.Restored", RestoredPaths.Count, Join(RestoredPaths)));
        }

        if (NotRestoredPaths.Count > 0)
        {
            lines.Add(Strings.Current.Plural(
                "Core.DryRun.ConfigSnapshot.NotRestored", NotRestoredPaths.Count, Join(NotRestoredPaths)));
        }

        if (RemovedPaths.Count > 0)
        {
            lines.Add(Strings.Current.Plural("Core.DryRun.ConfigSnapshot.Removed.Base", RemovedPaths.Count)
                + (RemovedTo.Length == 0
                    ? string.Empty
                    : Strings.Current.Format("Core.DryRun.ConfigSnapshot.Removed.KeptIn", RemovedTo))
                + Strings.Current.Format("Core.DryRun.ConfigSnapshot.Removed.Tail", Join(RemovedPaths)));
        }

        if (NotRemovedPaths.Count > 0)
        {
            lines.Add(Strings.Current.Plural(
                "Core.DryRun.ConfigSnapshot.NotRemoved", NotRemovedPaths.Count, Join(NotRemovedPaths)));
        }

        return lines;
    }

    private static string Join(IReadOnlyList<string> paths) => string.Join("; ", paths);
}

// The dry run ends by exiting the game abruptly, and the hazard is that the game writes something on
// shutdown that an abrupt exit corrupts, LauncherData.xml above all. This removes the question
// rather than answering it: everything is copied before the run and put back afterwards,
// unconditionally, so whatever the game does or does not write, the user's state is what it was.
//
// What actually changed is reported rather than swallowed. That turns the assumption into a fact and
// may allow the snapshot to be relaxed later.
public sealed class LaunchConfigSnapshot : IDisposable
{
    private const string BeforeFolder = "before";

    private const string AddedFolder = "added";

    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _notProtected = [];

    // Kept past the end of Restore so Dispose can tell a run that put everything back from one that did
    // not. The "before" copies are only spent once the files they cover are back where they came from.
    private readonly List<string> _notRestored = [];

    // Every file that was there before the run, whether or not the copy of it succeeded. The restore
    // moves aside what the run added, and "added" has to mean "was not there", not "BEM holds a copy
    // of it": LauncherData.xml lives inside Configs, so a copy that failed once meant the user's own
    // load order was moved out of Configs as if the run had created it.
    private readonly HashSet<string> _existed = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _root;

    private readonly string _configsPath;

    private readonly bool _configsExisted;

    private bool _restored;

    private LaunchConfigSnapshot(string root, string configsPath, bool configsExisted)
    {
        _root = root;
        _configsPath = configsPath;
        _configsExisted = configsExisted;
    }

    public string SnapshotRoot => _root;

    public static LaunchConfigSnapshot Capture(string launcherDataPath, string configsPath, string snapshotRoot)
    {
        var configsExisted = Directory.Exists(configsPath);
        var snapshot = new LaunchConfigSnapshot(snapshotRoot, configsPath, configsExisted);

        Directory.CreateDirectory(Path.Combine(snapshotRoot, BeforeFolder));

        if (configsExisted)
        {
            foreach (var file in Directory.EnumerateFiles(configsPath, "*", SearchOption.AllDirectories))
                snapshot.Take(file, Path.Combine("configs", Path.GetRelativePath(configsPath, file)));
        }

        // LauncherData.xml normally lives inside Configs, but BEM lets the user point at another
        // file, and that one still has to be covered.
        if (File.Exists(launcherDataPath) && !snapshot._existed.Contains(Full(launcherDataPath)))
            snapshot.Take(launcherDataPath, Path.Combine("launcher", Path.GetFileName(launcherDataPath)));

        return snapshot;
    }

    public LaunchConfigRestoreResult Restore()
    {
        var restoredPaths = new List<string>();
        var notRestoredPaths = new List<string>();
        var removedPaths = new List<string>();
        var notRemovedPaths = new List<string>();

        foreach (var (original, copy) in _files)
        {
            try
            {
                if (File.Exists(original) && SameContent(original, copy))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                File.Copy(copy, original, overwrite: true);
                restoredPaths.Add(original);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that will not restore must not strand the rest. It is kept apart from the
                // ones that went back, because calling it "put back" is the opposite of what happened.
                notRestoredPaths.Add(original);
            }
        }

        if (_configsExisted && Directory.Exists(_configsPath))
        {
            foreach (var file in Directory.EnumerateFiles(_configsPath, "*", SearchOption.AllDirectories))
            {
                if (_existed.Contains(Full(file)))
                    continue;

                if (MoveAside(file))
                    removedPaths.Add(file);
                else
                    notRemovedPaths.Add(file);
            }
        }

        _restored = true;
        _notRestored.Clear();
        _notRestored.AddRange(notRestoredPaths);

        return new LaunchConfigRestoreResult(
            restoredPaths.Count,
            removedPaths.Count,
            [.. restoredPaths, .. notRestoredPaths, .. removedPaths, .. notRemovedPaths],
            [.. _notProtected],
            [.. restoredPaths],
            [.. notRestoredPaths],
            [.. removedPaths],
            Path.Combine(_root, AddedFolder),
            [.. notRemovedPaths]);
    }

    public void Dispose()
    {
        if (!_restored)
            Restore();

        // The "before" copies have done their job, but anything moved aside is a file that only
        // exists here now, so the folder only goes away when it holds nothing of the user's.
        try
        {
            // A file that would not restore is a file whose only untouched version is the copy in
            // "before". Clearing that folder because the restore has run, without asking whether the
            // restore worked, is the safety net throwing away the one thing it exists to hold.
            if (_notRestored.Count > 0)
                return;

            var added = Path.Combine(_root, AddedFolder);

            if (Directory.Exists(added) && Directory.EnumerateFileSystemEntries(added).Any())
            {
                Directory.Delete(Path.Combine(_root, BeforeFolder), recursive: true);
                return;
            }

            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // A snapshot folder that will not delete is clutter, not lost data.
        }
    }

    private void Take(string source, string relativeCopy)
    {
        _existed.Add(Full(source));

        try
        {
            var copy = Path.Combine(_root, BeforeFolder, relativeCopy);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(source, copy, overwrite: true);
            _files[Full(source)] = copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be copied cannot be protected, and refusing the whole dry run over
            // one unreadable config would be worse than running without a copy of it. It is recorded
            // so the run can say which files it could not stand behind.
            _notProtected.Add(source);
        }
    }

    // Never deleted: a file the run created is moved into the snapshot folder, where the user can
    // still get it back.
    private bool MoveAside(string path)
    {
        try
        {
            var destination = Path.Combine(
                _root, AddedFolder, Path.GetRelativePath(_configsPath, path));

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(path, destination, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    private static bool SameContent(string left, string right)
    {
        try
        {
            var a = new FileInfo(left);
            var b = new FileInfo(right);

            return a.Exists && b.Exists && a.Length == b.Length
                && File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
