using System.Globalization;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Install;

public sealed record InstallLogRecovery(
    IReadOnlyList<ModuleArchiveLink> Recovered,
    int LogFilesRead,
    int ArchivesInstalled,
    int FoldersNoLongerInstalled)
{
    public static InstallLogRecovery Nothing { get; } = new([], 0, 0, 0);

    public string Describe()
    {
        if (LogFilesRead == 0)
            return Strings.Current["Core.Install.LogLinks.NoLog"];

        if (ArchivesInstalled == 0)
            return Strings.Current["Core.Install.LogLinks.NoCompletedInstall"];

        var gone = FoldersNoLongerInstalled > 0
            ? Strings.Current.Plural("Core.Install.LogLinks.FoldersGone", FoldersNoLongerInstalled)
            : string.Empty;

        return Recovered.Count == 0
            ? Strings.Current.Plural("Core.Install.LogLinks.AllHadIds", ArchivesInstalled, gone)
            : Strings.Current.Plural("Core.Install.LogLinks.Recovered", Recovered.Count, ArchivesInstalled, gone);
    }
}

// BEM writes a line to its own log naming every archive it installs, and a line naming the folder each
// module inside that archive lands in. For a module installed before BEM began keeping
// module-archive-links.json, that log is the only surviving record of where the module came from, and
// the archive's name is where the Nexus mod id lives.
//
// Nothing here is inferred from a name resembling another name. It is BEM's own account of what it
// did, read back, and a folder counts only when the module sitting in it today can still be read off
// disk. An install the log does not record as having completed is not a record of anything.
public static class InstallLogArchiveLinks
{
    public const string LogFileSearchPattern = "log_*.txt";

    public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    // Written by LoggingService, and named here so the side that reads them and the side that writes
    // them cannot drift apart into this quietly finding nothing.
    public const string ArchiveStarted = "Installing contents of archive: ";

    public const string ArchiveInstalled = "Successfully installed archive: ";

    public const string ArchiveFailed = "Failed to install archive: ";

    public const string DirectoryCopied = "Copying directory: ";

    public const string CopyDestinationSeparator = " -> ";

    public const string DirectoryUnblocked = "Unblocking directory: ";

    // What BEM said instead until August 2026. A log written then is still a record of what happened,
    // and reading only the current wording would throw away every install older than the rename.
    public const string ArchiveStartedBeforeAugust2026 = "Installing modules from archive: ";

    // A recovered link naming a different mod page from one the user ticked is BEM correcting itself
    // with stronger evidence, because what BEM logged itself installing outranks a page it suggested
    // and the user agreed with. Doing that quietly would be an overwrite; saying it is a correction.
    public static IReadOnlyList<string> Corrections(
        IReadOnlyList<ModuleArchiveLink> recovered,
        IReadOnlyDictionary<string, int> confirmedNexusModIdsByModuleId)
    {
        ArgumentNullException.ThrowIfNull(recovered);
        ArgumentNullException.ThrowIfNull(confirmedNexusModIdsByModuleId);

        return
        [
            .. recovered
                .Where(link => link.NexusModId is not null
                               && confirmedNexusModIdsByModuleId.TryGetValue(link.ModuleId, out var confirmed)
                               && confirmed != link.NexusModId)
                .OrderBy(link => link.ModuleId, StringComparer.OrdinalIgnoreCase)
                .Select(link => Strings.Current.Format(
                    "Core.Install.LogLinks.Correction",
                    link.ModuleId,
                    confirmedNexusModIdsByModuleId[link.ModuleId],
                    link.ArchiveFileName,
                    link.NexusModId))
        ];
    }

    public static InstallLogRecovery Recover(
        string logFolderPath,
        string modulesFolderPath,
        IReadOnlyCollection<string> installedModuleIds,
        IReadOnlyList<ModuleArchiveLink> alreadyRecorded,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(installedModuleIds);
        ArgumentNullException.ThrowIfNull(alreadyRecorded);

        var files = LogFiles(logFolderPath);

        if (files.Count == 0)
            return InstallLogRecovery.Nothing;

        var read = Read(files.SelectMany(ReadLines), ModuleIdsByFolderName(modulesFolderPath), modulesFolderPath);

        var recovered = ModuleArchiveRecovery.Recover(
            read.Candidates,
            installedModuleIds,
            alreadyRecorded,
            now,
            ModuleArchiveEvidence.InstallLog);

        return new InstallLogRecovery(
            recovered.Recovered,
            files.Count,
            read.ArchivesInstalled,
            read.FoldersNoLongerInstalled);
    }

    public sealed record InstallLogReading(
        IReadOnlyList<ArchiveModuleCandidate> Candidates,
        int ArchivesInstalled,
        int FoldersNoLongerInstalled);

    // Kept free of the file system so the grammar can be tested against the lines BEM actually wrote
    // rather than against a folder somebody has to build first.
    public static InstallLogReading Read(
        IEnumerable<string> logLines,
        IReadOnlyDictionary<string, string> moduleIdsByFolderName,
        string modulesFolderPath)
    {
        ArgumentNullException.ThrowIfNull(logLines);
        ArgumentNullException.ThrowIfNull(moduleIdsByFolderName);

        var byArchive = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var startedAt = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        string? archive = null;
        DateTimeOffset? openedAt = null;
        var pending = new List<string>();
        var installed = 0;
        var missing = 0;

        foreach (var line in logLines)
        {
            if ((After(line, ArchiveStarted) ?? After(line, ArchiveStartedBeforeAugust2026)) is { Length: > 0 } opened)
            {
                archive = opened;
                openedAt = TimestampOf(line);
                pending.Clear();
                continue;
            }

            if (archive is null)
                continue;

            // An install that never reported success put nothing on disk that can be attributed to it,
            // so what it was about to copy is dropped rather than recorded.
            if (After(line, ArchiveFailed) is not null)
            {
                archive = null;
                pending.Clear();
                continue;
            }

            if (After(line, ArchiveInstalled) is not null)
            {
                if (pending.Count > 0)
                {
                    if (!byArchive.TryGetValue(archive, out var modules))
                    {
                        byArchive[archive] = modules = [];
                        startedAt[archive] = openedAt ?? DateTimeOffset.MinValue;
                    }

                    foreach (var moduleId in pending.Where(id => !modules.Contains(id, StringComparer.OrdinalIgnoreCase)))
                        modules.Add(moduleId);
                }

                installed++;
                archive = null;
                pending.Clear();
                continue;
            }

            if (DestinationFolder(line, modulesFolderPath) is not { } folder)
                continue;

            if (!moduleIdsByFolderName.TryGetValue(folder, out var module))
            {
                missing++;
                continue;
            }

            if (!pending.Contains(module, StringComparer.OrdinalIgnoreCase))
                pending.Add(module);
        }

        var candidates = byArchive
            .Where(entry => entry.Value.Count > 0)
            .Select(entry => new ArchiveModuleCandidate(
                entry.Key,
                entry.Value,
                startedAt.TryGetValue(entry.Key, out var at) && at != DateTimeOffset.MinValue ? at : null))
            .ToList();

        return new InstallLogReading(candidates, installed, missing);
    }

    // Only a folder sitting directly in the game's Modules folder is a module install. A copy into a
    // sub-folder, into a temporary folder, or anywhere else says nothing about which mod page a module
    // came from.
    private static string? DestinationFolder(string line, string modulesFolderPath)
    {
        var destination = After(line, DirectoryCopied) is { } copied
            ? copied.IndexOf(CopyDestinationSeparator, StringComparison.Ordinal) is var separator and >= 0
                ? copied[(separator + CopyDestinationSeparator.Length)..].Trim()
                : null
            : After(line, DirectoryUnblocked);

        if (string.IsNullOrWhiteSpace(destination) || string.IsNullOrWhiteSpace(modulesFolderPath))
            return null;

        var trimmed = destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        var folder = Path.GetFileName(trimmed);

        return !string.IsNullOrWhiteSpace(folder)
               && parent is not null
               && string.Equals(
                   parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   modulesFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase)
            ? folder
            : null;
    }

    public static IReadOnlyDictionary<string, string> ModuleIdsByFolderName(string modulesFolderPath)
    {
        var byFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(modulesFolderPath) || !Directory.Exists(modulesFolderPath))
            return byFolder;

        try
        {
            foreach (var folder in Directory.EnumerateDirectories(modulesFolderPath))
            {
                var manifest = Path.Combine(folder, "SubModule.xml");

                if (File.Exists(manifest) && SubModuleXmlParser.TryRecoverDeclaredId(manifest) is { } moduleId)
                    byFolder[Path.GetFileName(folder)] = moduleId;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return byFolder;
    }

    // Named log_yyyyMMdd.txt, so sorting by name is sorting by day, which is what puts the earliest
    // install of a module before the latest.
    private static IReadOnlyList<string> LogFiles(string logFolderPath)
    {
        if (string.IsNullOrWhiteSpace(logFolderPath) || !Directory.Exists(logFolderPath))
            return [];

        try
        {
            return [.. Directory.EnumerateFiles(logFolderPath, LogFileSearchPattern).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        try
        {
            return File.ReadLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? After(string line, string token)
    {
        var at = line.IndexOf(token, StringComparison.Ordinal);

        return at < 0 ? null : line[(at + token.Length)..].Trim();
    }

    // "[2026-08-12 05:27:47.286] [Info] ...", written in this machine's own local time.
    private static DateTimeOffset? TimestampOf(string line)
    {
        if (line.Length < TimestampFormat.Length + 2 || line[0] != '[')
            return null;

        return DateTimeOffset.TryParseExact(
            line.AsSpan(1, TimestampFormat.Length),
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var at)
            ? at.ToUniversalTime()
            : null;
    }
}
