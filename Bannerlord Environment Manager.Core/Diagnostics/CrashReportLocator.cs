using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public enum CrashReportSource
{
    Game,
    ButterLib,
    CrashDoctor,
    Captured
}

public sealed record CrashReportFolder(string Path, CrashReportSource Source);

public sealed record DiscoveredCrashReport(
    string Path,
    CrashReportSource Source,
    DateTimeOffset Written,
    CrashReport Report,
    int Occurrences = 1)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed record CrashReportSearch(
    IReadOnlyList<DiscoveredCrashReport> Reports,
    IReadOnlyList<string> Searched,
    IReadOnlyList<string> Unreadable);

// Reads crash reports that already exist on disk. BEM is never in the crash path: whatever wrote the
// report - the game, ButterLib or CrashDoctor - wrote it without BEM's involvement, and this only
// finds it afterwards.
public static class CrashReportLocator
{
    private const string CrashFolderName = "crashes";
    private const string OccurrencesFileName = "occurrences.txt";

    private static readonly string[] ReadableExtensions = [".txt", ".html", ".htm"];

    public static IReadOnlyList<CrashReportFolder> DefaultFolders(
        string? gameInstallPath, InstanceDataRoot? dataRoot = null)
    {
        var root = dataRoot ?? InstanceDataRoot.ForMachine();

        var folders = new List<CrashReportFolder>
        {
            // First, because it is the only copy that survives: the game's own uploader deletes the
            // folders below it five seconds after it has finished sending them.
            new(CrashArtifactPaths.GetDefaultRoot(dataRoot), CrashReportSource.Captured),
            new(Path.Combine(root.ProgramData, CrashFolderName), CrashReportSource.Game),
            new(Path.Combine(root.Documents, CrashFolderName), CrashReportSource.ButterLib)
        };

        if (!string.IsNullOrWhiteSpace(gameInstallPath))
        {
            var crashDoctor = Path.Combine(gameInstallPath, "Modules", "CrashDoctor");

            folders.Add(new CrashReportFolder(
                Path.Combine(crashDoctor, "managed_reports"),
                CrashReportSource.CrashDoctor));

            // CrashDoctor redirects the game's own crash folder here, so on an install that has it
            // the ProgramData path above is a link to this one and the two collapse into one search.
            folders.Add(new CrashReportFolder(Path.Combine(crashDoctor, "cache"), CrashReportSource.Game));
        }

        return folders;
    }

    public static CrashReportSearch Find(string? gameInstallPath, InstanceDataRoot? dataRoot = null) =>
        Find(DefaultFolders(gameInstallPath, dataRoot));

    public static CrashReportSearch Find(IEnumerable<CrashReportFolder> folders)
    {
        var reports = new List<DiscoveredCrashReport>();
        var searched = new List<string>();
        var unreadable = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder.Path) || !visited.Add(Identify(folder.Path)))
                continue;

            searched.Add(folder.Path);

            if (!Directory.Exists(folder.Path))
                continue;

            foreach (var file in ReadableFiles(folder.Path, unreadable))
                Read(file, folder.Source, reports, unreadable);
        }

        return new CrashReportSearch(
            [.. reports.OrderByDescending(r => r.Written).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)],
            searched,
            unreadable);
    }

    // Two of the default folders are the same folder on an install with CrashDoctor, one of them
    // reached through a directory link. Searching both would list every report twice.
    private static string Identify(string path)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

            return Directory.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName is { } target
                ? Path.TrimEndingDirectorySeparator(target)
                : full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return path;
        }
    }

    private static IReadOnlyList<string> ReadableFiles(string folder, List<string> unreadable)
    {
        try
        {
            return
            [
                .. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(file => ReadableExtensions.Contains(
                        Path.GetExtension(file),
                        StringComparer.OrdinalIgnoreCase))
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable.Add($"{folder} could not be read: {ex.Message}");

            return [];
        }
    }

    private static void Read(
        string path,
        CrashReportSource source,
        List<DiscoveredCrashReport> reports,
        List<string> unreadable)
    {
        try
        {
            var report = CrashReport.Parse(CrashReportText.Extract(File.ReadAllText(path)));

            // A crash folder also holds notes, occurrence lists, settings and logs. A file with no
            // exception in it is not a report BEM failed to read, it is not a report.
            if (report.Failed || !report.NamesException)
                return;

            reports.Add(new DiscoveredCrashReport(
                path,
                source,
                File.GetLastWriteTimeUtc(path),
                report,
                CountOccurrences(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable.Add($"{path} could not be read: {ex.Message}");
        }
    }

    private static int CountOccurrences(string reportPath)
    {
        var siblings = Path.GetDirectoryName(reportPath);

        if (siblings is null)
            return 1;

        try
        {
            var occurrences = Path.Combine(siblings, OccurrencesFileName);

            return File.Exists(occurrences)
                ? Math.Max(File.ReadAllLines(occurrences).Count(line => !string.IsNullOrWhiteSpace(line)), 1)
                : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 1;
        }
    }
}
