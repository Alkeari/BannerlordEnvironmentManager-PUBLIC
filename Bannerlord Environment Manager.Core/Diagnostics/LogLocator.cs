using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public enum LogSource
{
    Module,
    ButterLib,
    Game,
    CrashDoctor,
    Captured
}

public sealed record LogFolder(string Path, LogSource Source, int MaxDepth = 8, bool IncludeTextFiles = false);

public sealed record DiscoveredLog(
    string Path,
    LogSource Source,
    string Owner,
    bool OwnerIsCertain,
    long SizeBytes,
    DateTimeOffset Written)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed record LogSearch(
    IReadOnlyList<DiscoveredLog> Logs,
    IReadOnlyList<string> Searched,
    IReadOnlyList<string> Unreadable);

// Lists the log files already on disk. Every mod that logs at all writes here, so this finds far more
// than a crash folder does and finds it on a run that never crashed. It reads names, sizes and
// timestamps only: nothing here opens a file, and nothing here writes one.
public static class LogLocator
{
    private static readonly Regex Rotation = new(@"[._-]\d{1,6}$", RegexOptions.Compiled);
    private static readonly Regex Stamp = new(@"[._-]?\d{8}$", RegexOptions.Compiled);
    private static readonly Regex DebugSuffix = new(@"[._-][Dd]ebug$", RegexOptions.Compiled);
    private static readonly Regex AllDigits = new(@"^\d+$", RegexOptions.Compiled);

    // Stems that name the logger rather than a module. Every mod using ButterLib's shared sinks lands
    // in these files, so naming one module from the file name would name the wrong mod.
    private static readonly string[] SharedStems =
        ["default", "trace", "log", "logs", "session", "debug", "output", "state"];

    // Words that merely contain "log" and never name one. CHANGELOG.txt and sas_changelog.txt are both
    // real files in this install's Modules folder, and neither is a log.
    private static readonly string[] NotLogWords =
        ["changelog", "catalog", "dialog", "analog", "prolog", "epilog"];

    private static readonly Dictionary<string, string> KnownWriters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["butterlib"] = "ButterLib",
        ["crashdoctor"] = "CrashDoctor"
    };

    public static IReadOnlyList<LogFolder> DefaultFolders(string? gameInstallPath, InstanceDataRoot? dataRoot = null)
    {
        var root = dataRoot ?? InstanceDataRoot.ForMachine();
        var documents = root.Documents;
        var shared = root.ProgramData;

        var folders = new List<LogFolder>
        {
            // BEM's own copies come first. They are the only ones left after a crash, because the
            // game's uploader deletes the originals once it has sent them.
            new(CrashArtifactPaths.GetDefaultRoot(dataRoot), LogSource.Captured, IncludeTextFiles: true),

            // ButterLib's own folder comes first so its files keep its source rather than the sweep
            // of Configs that also reaches them.
            new(Path.Combine(documents, "Configs", "ModLogs"), LogSource.ButterLib, IncludeTextFiles: true),

            // rgl_log, rgl_log_errors, watchdog_log and crashlist. All .txt, and all written by the
            // engine itself, which is why a crash leaves its trace here and nowhere in Documents.
            new(Path.Combine(shared, "logs"), LogSource.Game, MaxDepth: 0, IncludeTextFiles: true),

            // A mod that logs to the shared folder writes into a folder of its own beside logs, the way
            // ATC leaves ATC.debug.log there. One level down reaches those and stops short of the crash
            // folders, which the crash locator lists instead.
            new(shared, LogSource.Game, MaxDepth: 1),
            new(documents, LogSource.Module, MaxDepth: 0),
            new(Path.Combine(documents, "Logs"), LogSource.Module),
            new(Path.Combine(documents, "Configs"), LogSource.Module),
            new(Path.Combine(documents, "CrashDoctor", "state"), LogSource.CrashDoctor)
        };

        if (!string.IsNullOrWhiteSpace(gameInstallPath))
        {
            // Both platforms, so a Game Pass install's logs are swept too.
            foreach (var binary in GameInstallLocator.GetBinaryFolders(gameInstallPath))
            {
                folders.Add(new LogFolder(
                    Path.Combine(gameInstallPath, "bin", binary),
                    LogSource.Game,
                    MaxDepth: 0));
            }

            // A module that logs beside its own folder writes at the top of it. Descending further
            // walks every asset folder in the install for nothing.
            folders.Add(new LogFolder(Path.Combine(gameInstallPath, "Modules"), LogSource.Module, MaxDepth: 1));
        }

        return folders;
    }

    public static LogSearch Find(string? gameInstallPath, InstanceDataRoot? dataRoot = null) =>
        Find(DefaultFolders(gameInstallPath, dataRoot));

    public static LogSearch Find(IEnumerable<LogFolder> folders)
    {
        var logs = new List<DiscoveredLog>();
        var searched = new List<string>();
        var unreadable = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder.Path))
                continue;

            searched.Add(folder.Path);

            if (!Directory.Exists(folder.Path))
                continue;

            foreach (var file in Walk(folder.Path, folder.Path, folder.MaxDepth, folder.IncludeTextFiles, unreadable))
            {
                if (!seen.Add(file))
                    continue;

                Describe(file, folder, logs, unreadable);
            }
        }

        return new LogSearch(
            [.. logs.OrderByDescending(l => l.Written).ThenBy(l => l.Path, StringComparer.OrdinalIgnoreCase)],
            searched,
            unreadable);
    }

    private static IEnumerable<string> Walk(
        string folder,
        string root,
        int remainingDepth,
        bool includeTextFiles,
        List<string> unreadable)
    {
        string[] files;
        string[] children;

        try
        {
            files = Directory.GetFiles(folder);
            children = remainingDepth > 0 ? Directory.GetDirectories(folder) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable.Add($"{folder} could not be read: {ex.Message}");

            yield break;
        }

        foreach (var file in files)
        {
            if (IsLog(file, includeTextFiles))
                yield return file;
        }

        foreach (var child in children)
        {
            foreach (var file in Walk(child, root, remainingDepth - 1, includeTextFiles, unreadable))
                yield return file;
        }
    }

    // Half the ecosystem writes .txt. SI_DebugLog.txt sits in the game's bin folder beside
    // steam_appid.txt, and the only thing that separates the two is the word in the name.
    private static bool IsLog(string path, bool includeTextFiles)
    {
        var extension = Path.GetExtension(path);

        if (string.Equals(extension, ".log", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase))
            return false;

        return includeTextFiles || NameSaysLog(Path.GetFileNameWithoutExtension(path));
    }

    // Taking the misleading word out first, rather than rejecting the whole name, keeps a file called
    // dialog_log.txt counted while CHANGELOG.txt is not.
    private static bool NameSaysLog(string stem)
    {
        foreach (var word in NotLogWords)
            stem = stem.Replace(word, string.Empty, StringComparison.OrdinalIgnoreCase);

        return stem.Contains("log", StringComparison.OrdinalIgnoreCase);
    }

    private static void Describe(
        string path,
        LogFolder folder,
        List<DiscoveredLog> logs,
        List<string> unreadable)
    {
        try
        {
            var file = new FileInfo(path);
            var (owner, certain) = ResolveOwner(path, folder.Path);

            logs.Add(new DiscoveredLog(
                path,
                folder.Source,
                owner,
                certain,
                file.Length,
                file.LastWriteTimeUtc));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable.Add($"{path} could not be read: {ex.Message}");
        }
    }

    private static (string Owner, bool Certain) ResolveOwner(string path, string root)
    {
        if (Names(Strip(Path.GetFileNameWithoutExtension(path))) is { } named)
            return (named, true);

        var parent = Path.GetDirectoryName(path);

        if (parent is null || Same(parent, root))
            return (string.Empty, false);

        return Names(Path.GetFileName(parent)) is { } folderNamed
            ? (folderNamed, true)
            : (string.Empty, false);
    }

    private static string? Names(string stem)
    {
        if (KnownWriters.TryGetValue(stem, out var writer))
            return writer;

        return stem.Length > 0
               && !AllDigits.IsMatch(stem)
               && !SharedStems.Contains(stem, StringComparer.OrdinalIgnoreCase)
            ? stem
            : null;
    }

    // Mod loggers stamp a date, a rotation index or a debug marker onto the file name. Those are the
    // logger's doing, not part of the module's name.
    private static string Strip(string stem)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            var before = stem;

            stem = DebugSuffix.Replace(stem, string.Empty);
            stem = Rotation.Replace(stem, string.Empty);
            stem = Stamp.Replace(stem, string.Empty);
            stem = stem.TrimEnd('.', '_', '-', ' ');

            if (stem == before)
                break;
        }

        return stem;
    }

    private static bool Same(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
