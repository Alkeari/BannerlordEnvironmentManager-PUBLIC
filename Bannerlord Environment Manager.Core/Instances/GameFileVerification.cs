using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace BannerlordEnvironmentManager.Core.Instances;

// One file a depot's manifest says the game ships: where it lives under the game folder, how large it
// is and the SHA-1 Steam recorded for its whole content. AppId says which Steam app (the base game or
// a DLC) the depot belongs to, because a repair has to ask that app for the file.
public sealed record GameFileEntry(string RelativePath, long Size, string? Sha1, int AppId, string DepotId);

// The readable manifest DepotDownloader writes beside a run that was given -manifest-only
// (manifest_<depot>_<manifest>.txt): a header, then one row per entry of size, chunk count, SHA-1,
// flags and name. A directory carries flag 64 and holds no content, so it is not a file to verify.
public static partial class GameFileManifestText
{
    public const string FileNamePattern = "manifest_*.txt";

    private const int DirectoryFlag = 64;

    [GeneratedRegex(@"^\s*(?<size>\d+)\s+(?<chunks>\d+)\s+(?<sha>[0-9a-fA-F]{40})\s+(?<flags>\d+)\s+(?<name>\S.*?)\s*$")]
    private static partial Regex RowPattern { get; }

    public static IReadOnlyList<GameFileEntry> Parse(string? text, int appId, string depotId)
    {
        var entries = new List<GameFileEntry>();

        if (string.IsNullOrWhiteSpace(text))
            return entries;

        foreach (var line in text.Split('\n'))
        {
            var match = RowPattern.Match(line.TrimEnd('\r'));

            if (!match.Success
                || !long.TryParse(match.Groups["size"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || !int.TryParse(match.Groups["flags"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var flags)
                || (flags & DirectoryFlag) != 0)
                continue;

            entries.Add(new GameFileEntry(
                Normalize(match.Groups["name"].Value), size, match.Groups["sha"].Value.ToLowerInvariant(), appId, depotId));
        }

        return entries;
    }

    // Forward slashes, no leading separator, so one spelling names a file on every comparison.
    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}

public enum GameFileProblem
{
    Missing,
    WrongSize,
    WrongContent
}

public sealed record GameFileFinding(GameFileEntry Entry, GameFileProblem Problem);

// What a verification found. Checked counts the files compared.
public sealed record GameFileReport(
    int Checked,
    IReadOnlyList<GameFileFinding> Findings,
    IReadOnlyList<string> Extra)
{
    public IReadOnlyList<GameFileFinding> Missing => [.. Findings.Where(f => f.Problem == GameFileProblem.Missing)];

    public IReadOnlyList<GameFileFinding> Changed => [.. Findings.Where(f => f.Problem != GameFileProblem.Missing)];

    public bool IsClean => Findings.Count == 0;

    // Extra files are reported and never removed: a file the manifest does not list may be something the
    // player put there on purpose, and deleting it would be BEM's guess.
    public bool NeedsRepair => Findings.Count > 0;
}

// Checks an instance's own game files against the file list Steam publishes for its version. Only what
// the manifest covers is judged: Modules folders the manifest has no file in are community modules,
// and DepotDownloader's own bookkeeping is never the game's.
public static class GameFileVerifier
{
    private static readonly string[] OwnFolders = [".DepotDownloader"];

    public static GameFileReport Verify(
        string gameFolder,
        IEnumerable<GameFileEntry> expected,
        bool hashContent = true,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        ArgumentNullException.ThrowIfNull(expected);

        // A path two depots both publish is the same file, so it is judged once.
        var entries = expected
            .GroupBy(e => GameFileManifestText.Normalize(e.RelativePath), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var findings = new List<GameFileFinding>();
        var done = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = Path.Combine(gameFolder, GameFileManifestText.Normalize(entry.RelativePath).Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(path);

            if (!info.Exists)
                findings.Add(new GameFileFinding(entry, GameFileProblem.Missing));
            else if (info.Length != entry.Size)
                findings.Add(new GameFileFinding(entry, GameFileProblem.WrongSize));
            else if (hashContent && entry.Sha1 is { Length: > 0 } sha
                && !string.Equals(Sha1Of(path, cancellationToken), sha, StringComparison.OrdinalIgnoreCase))
                findings.Add(new GameFileFinding(entry, GameFileProblem.WrongContent));

            progress?.Report(++done);
        }

        return new GameFileReport(entries.Count, findings, ExtraFiles(gameFolder, entries, cancellationToken));
    }

    private static List<string> ExtraFiles(string gameFolder, List<GameFileEntry> entries, CancellationToken cancellationToken)
    {
        var known = entries.Select(e => GameFileManifestText.Normalize(e.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // An official module is a Modules folder the manifest ships files in; any other is a community
        // module and none of this check's business.
        var officialModules = entries
            .Select(e => GameFileManifestText.Normalize(e.RelativePath).Split('/'))
            .Where(parts => parts.Length > 2 && parts[0].Equals("Modules", StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[1])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var extra = new List<string>();

        if (!Directory.Exists(gameFolder))
            return extra;

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };

        foreach (var file in Directory.EnumerateFiles(gameFolder, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = GameFileManifestText.Normalize(Path.GetRelativePath(gameFolder, file));
            var parts = relative.Split('/');

            if (OwnFolders.Contains(parts[0], StringComparer.OrdinalIgnoreCase)
                || (parts.Length == 1 && parts[0].StartsWith("manifest_", StringComparison.OrdinalIgnoreCase)
                    && parts[0].EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                || known.Contains(relative))
                continue;

            if (parts.Length > 2 && parts[0].Equals("Modules", StringComparison.OrdinalIgnoreCase) && !officialModules.Contains(parts[1]))
                continue;

            extra.Add(relative);
        }

        extra.Sort(StringComparer.OrdinalIgnoreCase);
        return extra;
    }

    private static string Sha1Of(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
        using var sha = SHA1.Create();
        var buffer = new byte[1 << 20];
        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }
}

// The repair for a managed instance: DepotDownloader is asked for just the files that differ, one run
// per Steam app, through the same invocation the instance download uses (-validate included).
public static class GameFileRepair
{
    // One app's files, the list DepotDownloader's -filelist reads: a path a line, forward slashes.
    public static IReadOnlyDictionary<int, string> FileListsByApp(GameFileReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return report.Findings
            .GroupBy(f => f.Entry.AppId)
            .ToDictionary(
                g => g.Key,
                g => string.Join('\n', g.Select(f => GameFileManifestText.Normalize(f.Entry.RelativePath)).Order(StringComparer.Ordinal)) + "\n");
    }

    // The Steam client's own validate, for an install Steam owns: the client does the comparison and
    // the repair. The caller opens it; the scheme is built here so nothing in Core ever opens it.
    public static Uri SteamValidateUri(int appId) =>
        new(string.Concat("steam", ":", "//validate/", appId.ToString(CultureInfo.InvariantCulture)));
}
