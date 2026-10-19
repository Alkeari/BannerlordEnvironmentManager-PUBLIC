using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Safety;

public sealed record ModSafetyRequest(
    string GameInstallPath,
    string? ArchivesFolderPath,
    SafetyBlocklistIndex Blocklist,
    SafetyBlocklistState BlocklistState,
    AuthenticodeSignerLookup? Signer = null,
    OfficialSignatureCheck? OfficialCheck = null,
    SafetyArchiveExtraction? ArchiveExtraction = null);

// Opens an archive format the scanner cannot read itself, currently .7z and .rar, and puts only the
// files matching the given patterns into a folder of its own. Returning null means it could not be
// opened at all, which stays "could not look" and never becomes a clean result.
//
// The scanner owns the folder it is handed and removes it as soon as it has finished reading it.
public delegate string? SafetyArchiveExtraction(
    string archivePath,
    IReadOnlyList<string> includePatterns,
    CancellationToken cancellationToken);

// Reads installed modules, Workshop subscriptions and downloaded archives, and reports what matches
// the trojanized-mod fingerprint. It never deletes or runs anything, and every action a finding
// offers is a separate, explicit choice the user makes afterwards.
//
// The one thing it writes is the temporary folder a SafetyArchiveExtraction fills for a .7z or a
// .rar, which it removes again the moment it has read it. Nothing inside is ever executed.
public static class ModSafetyScanner
{
    private const long MaximumAssemblyBytes = 64L * 1024 * 1024;

    private const long MaximumScriptBytes = 4L * 1024 * 1024;

    private const long MaximumArchiveReadBytes = 1024L * 1024 * 1024;

    private static readonly string[] AssemblyExtensions = [".dll", ".exe"];

    // Directly runnable files that ride along with a mod. Shipping one is NOT itself evidence: plenty
    // of legitimate mods ship an installer or a helper script, and grading that suspicious on its own
    // would fire on healthy installs. What is read is the script's own content, against the same
    // fingerprint every other file gets.
    private static readonly string[] ScriptExtensions =
        [".ps1", ".psm1", ".bat", ".cmd", ".vbs", ".vbe", ".jse", ".wsf", ".wsh", ".hta", ".scr", ".reg", ".lnk"];

    // Every extension the scanner would read, as wildcards an external extractor understands. Handing
    // these over rather than asking for the whole archive is what keeps opening a .7z cheap: a mod's
    // textures and meshes are the bulk of it and none of them are ever inspected.
    public static IReadOnlyList<string> CodeFilePatterns { get; } =
        [.. AssemblyExtensions.Concat(ScriptExtensions).Select(extension => $"*{extension}")];

    public static ModSafetyReport Scan(ModSafetyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var results = new List<SafetyScanResult>();
        var officialSkipped = 0;
        var archivesScanned = 0;
        string? error = null;

        if (!string.IsNullOrWhiteSpace(request.GameInstallPath))
        {
            var scan = ModuleScanner.ScanAll(request.GameInstallPath, request.OfficialCheck);

            if (scan.Failed)
            {
                error = scan.Error;
            }
            else
            {
                foreach (var module in scan.Modules)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // The game's own modules are the game's own code. A mod cannot hide here: a module
                    // that only claims to be official has already been demoted by the claim audit
                    // ScanAll ran, so what is skipped is signature-corroborated or vouched for.
                    if (module.IsOfficial)
                    {
                        officialSkipped++;
                        continue;
                    }

                    results.Add(ScanModule(module, request.Blocklist, request.Signer, cancellationToken));
                }
            }
        }

        foreach (var archive in Archives(request.ArchivesFolderPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            archivesScanned++;
            results.Add(ScanArchive(archive, request.Blocklist, request.ArchiveExtraction, cancellationToken));
        }

        stopwatch.Stop();

        if (stopwatch.ElapsedMilliseconds >= 20)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bem_perf.log"),
                    $"[P] {DateTime.Now:HH:mm:ss.fff} ModSafetyScanner.Scan elapsed={stopwatch.ElapsedMilliseconds}ms thread={(Environment.CurrentManagedThreadId != 1 ? "W" : "UI")}\r\n");
            }
            catch
            {
            }
        }

        return new ModSafetyReport(
            [.. results.OrderByDescending(r => r.Verdict)
                .ThenByDescending(r => r.NothingCouldBeRead)
                .ThenBy(r => r.Target.Name, StringComparer.OrdinalIgnoreCase)],
            request.BlocklistState,
            officialSkipped,
            archivesScanned,
            error,
            stopwatch.Elapsed);
    }

    public static SafetyScanResult ScanModule(
        ModuleManifest module,
        SafetyBlocklistIndex blocklist,
        AuthenticodeSignerLookup? signer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(blocklist);

        var workshopId = WorkshopIdOf(module);
        var target = new SafetyTarget(
            module.Name,
            module.Id.Value,
            module.FolderPath,
            module.Source == ModuleSource.Workshop ? SafetyTargetKind.WorkshopModule : SafetyTargetKind.Module,
            workshopId,
            PageUrlOf(module, workshopId));

        var accumulator = new Accumulator(blocklist);

        if (blocklist.ByWorkshopId(workshopId) is { } reported)
            accumulator.RecordWorkshopReport(module.FolderPath, reported);

        foreach (var file in Files(module.FolderPath, accumulator))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanFile(file, accumulator, signer);
        }

        return accumulator.Build(target);
    }

    public static SafetyScanResult ScanArchive(
        string archivePath,
        SafetyBlocklistIndex blocklist,
        SafetyArchiveExtraction? extraction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blocklist);

        var target = new SafetyTarget(
            Path.GetFileName(archivePath),
            Strings.Current["Core.Safety.Scanner.DownloadedArchive"],
            archivePath,
            SafetyTargetKind.Archive);

        var accumulator = new Accumulator(blocklist);

        if (!Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ScanThrough7Zip(archivePath, extraction, accumulator, cancellationToken);
            return accumulator.Build(target);
        }

        ZipArchive archive;

        try
        {
            archive = ZipFile.OpenRead(archivePath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            accumulator.RecordUnreadable(archivePath, ex.Message);
            return accumulator.Build(target);
        }

        using (archive)
        {
            long read = 0;

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var extension = Path.GetExtension(entry.Name);
                var isAssembly = AssemblyExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
                var isScript = ScriptExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

                if (!isAssembly && !isScript)
                    continue;

                var limit = isAssembly ? MaximumAssemblyBytes : MaximumScriptBytes;

                if (entry.Length > limit)
                {
                    accumulator.RecordUnreadable(
                        $"{archivePath} -> {entry.FullName}",
                        Strings.Current.Format("Core.Safety.Scanner.TooLarge", entry.Length / 1024 / 1024));

                    continue;
                }

                if (read > MaximumArchiveReadBytes)
                {
                    accumulator.RecordUnreadable(
                        $"{archivePath} -> {entry.FullName}",
                        Strings.Current["Core.Safety.Scanner.ArchiveTooLarge"]);

                    continue;
                }

                byte[] bytes;

                try
                {
                    using var stream = entry.Open();
                    using var buffer = new MemoryStream();

                    stream.CopyTo(buffer);
                    bytes = buffer.ToArray();
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
                {
                    accumulator.RecordUnreadable($"{archivePath} -> {entry.FullName}", ex.Message);
                    continue;
                }

                read += bytes.Length;
                ScanArchiveEntry(archivePath, entry.FullName, bytes, isAssembly, accumulator);
            }
        }

        return accumulator.Build(target);
    }

    // A .7z or a .rar is read by having the caller's 7-Zip pull the code and script files out of it
    // into a folder of its own, which is read and then removed. The archive itself is never installed
    // and nothing extracted from it is ever run.
    private static void ScanThrough7Zip(
        string archivePath,
        SafetyArchiveExtraction? extraction,
        Accumulator accumulator,
        CancellationToken cancellationToken)
    {
        if (extraction is null)
        {
            accumulator.RecordUnreadable(
                archivePath,
                Strings.Current["Core.Safety.Scanner.NeedsSevenZip"]);

            return;
        }

        string? folder;

        try
        {
            folder = extraction(archivePath, CodeFilePatterns, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            accumulator.RecordUnreadable(archivePath, ex.Message);
            return;
        }

        if (folder is null)
        {
            accumulator.RecordUnreadable(archivePath, Toolkit.SevenZipRequirement.CouldNotOpen);

            return;
        }

        try
        {
            string Describe(string path) => $"{archivePath} -> {Relative(folder, path)}";

            foreach (var file in Files(folder, accumulator, Describe))
            {
                cancellationToken.ThrowIfCancellationRequested();

                byte[] bytes;

                try
                {
                    bytes = File.ReadAllBytes(file.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    accumulator.RecordUnreadable(Describe(file.Path), ex.Message);
                    continue;
                }

                ScanArchiveEntry(archivePath, Relative(folder, file.Path), bytes, file.IsAssembly, accumulator);
            }
        }
        finally
        {
            TryRemove(folder);
        }
    }

    private static string Relative(string folder, string path) =>
        Path.GetRelativePath(folder, path).Replace('\\', '/');

    private static void TryRemove(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
        }
    }

    private static void ScanArchiveEntry(
        string archivePath,
        string entryPath,
        byte[] bytes,
        bool isAssembly,
        Accumulator accumulator)
    {
        IReadOnlyList<string> literals;

        if (isAssembly)
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                var extracted = SafetyStrings.FromAssembly(stream);

                literals = extracted.Literals;
                accumulator.RecordCapabilities(extracted.Capabilities);
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException)
            {
                literals = SafetyStrings.FromBytes(bytes);
            }
        }
        else
        {
            literals = SafetyStrings.FromBytes(bytes);
        }

        accumulator.RecordFile(archivePath, entryPath, literals, () => Hash(bytes));
    }

    private static void ScanFile(ScannedFile file, Accumulator accumulator, AuthenticodeSignerLookup? signer)
    {
        IReadOnlyList<string> literals;

        if (file.IsAssembly)
        {
            try
            {
                using var stream = File.OpenRead(file.Path);
                var extracted = SafetyStrings.FromAssembly(stream);

                literals = extracted.Literals;
                accumulator.RecordCapabilities(extracted.Capabilities);

                if (signer is not null)
                    accumulator.RecordSigner(signer(file.Path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                accumulator.RecordUnreadable(file.Path, ex.Message);
                return;
            }
        }
        else
        {
            try
            {
                literals = SafetyStrings.FromBytes(File.ReadAllBytes(file.Path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                accumulator.RecordUnreadable(file.Path, ex.Message);
                return;
            }
        }

        accumulator.RecordFile(file.Path, entryPath: null, literals, () => HashFile(file.Path));
    }

    private sealed record ScannedFile(string Path, bool IsAssembly);

    // describe names a file the way the user will recognize it. For a module that is its path; for an
    // archive opened through 7-Zip the real path is a temporary folder that will not exist by the time
    // anyone reads the message, so it is named as the archive and the entry inside it instead.
    private static IEnumerable<ScannedFile> Files(
        string folderPath,
        Accumulator accumulator,
        Func<string, string>? describe = null)
    {
        string Name(string path) => describe is null ? path : describe(path);

        IEnumerable<string> paths;

        try
        {
            paths = Directory.EnumerateFiles(folderPath, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MatchCasing = MatchCasing.CaseInsensitive
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            accumulator.RecordUnreadable(Name(folderPath), ex.Message);
            yield break;
        }

        foreach (var path in paths)
        {
            var extension = Path.GetExtension(path);
            var isAssembly = AssemblyExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

            if (!isAssembly && !ScriptExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                continue;

            long length;

            try
            {
                length = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                accumulator.RecordUnreadable(Name(path), ex.Message);
                continue;
            }

            if (length > (isAssembly ? MaximumAssemblyBytes : MaximumScriptBytes))
            {
                accumulator.RecordUnreadable(Name(path), Strings.Current.Format("Core.Safety.Scanner.TooLarge", length / 1024 / 1024));
                continue;
            }

            yield return new ScannedFile(path, isAssembly);
        }
    }

    private static IEnumerable<string> Archives(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            yield break;

        string[] files;

        try
        {
            files = Directory.GetFiles(folderPath, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var extension = Path.GetExtension(file);

            if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
                yield return file;
        }
    }

    private static string? WorkshopIdOf(ModuleManifest module)
    {
        if (module.Source != ModuleSource.Workshop)
            return null;

        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(module.FolderPath));

        return leaf.Length > 0 && leaf.All(char.IsAsciiDigit) ? leaf : null;
    }

    private static string? PageUrlOf(ModuleManifest module, string? workshopId)
    {
        if (workshopId is not null)
            return $"https://steamcommunity.com/sharedfiles/filedetails/?id={workshopId}";

        return module.Url is { Length: > 0 } url
            && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ? url
            : null;
    }

    public static string? HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // Collects one target's findings. Hashes are computed lazily: for every assembly when the
    // blocklist actually carries hashes to compare against, and otherwise only for a file that has
    // already produced evidence, which is the file the user will want to look up.
    private sealed class Accumulator(SafetyBlocklistIndex blocklist)
    {
        private readonly List<FlaggedFile> flagged = [];
        private readonly List<UnreadableFile> unreadable = [];
        private readonly HashSet<SafetyCapability> capabilities = [];
        private readonly SortedSet<string> unrecognizedHosts = new(StringComparer.OrdinalIgnoreCase);
        private readonly SortedSet<string> signers = new(StringComparer.OrdinalIgnoreCase);
        private int signedFiles;
        private string? blocklistReason;
        private bool knownBad;
        private int filesRead;

        public void RecordCapabilities(IReadOnlyList<SafetyCapability> found)
        {
            foreach (var capability in found)
                capabilities.Add(capability);
        }

        public void RecordSigner(AuthenticodeSigner? signer)
        {
            if (!TrustedPublishers.IsTrusted(signer))
                return;

            signedFiles++;
            signers.Add(signer!.CommonName);
        }

        public void RecordUnreadable(string path, string reason) => unreadable.Add(new UnreadableFile(path, reason));

        public void RecordWorkshopReport(string folderPath, SafetyBlocklistEntry entry)
        {
            knownBad = true;
            blocklistReason = entry.Reason;

            flagged.Add(new FlaggedFile(folderPath, null, null,
            [
                new SafetyEvidence(
                    SafetySignal.BlocklistWorkshopId,
                    $"Workshop id {entry.WorkshopId}",
                    entry.Reason ?? Strings.Current["Core.Safety.Scanner.WorkshopReportFallback"])
            ]));
        }

        public void RecordFile(
            string filePath,
            string? entryPath,
            IReadOnlyList<string> literals,
            Func<string?> hash)
        {
            filesRead++;

            var evidence = SafetySignatures.Inspect(literals, unrecognizedHosts).ToList();
            string? sha256 = null;

            if (blocklist.MatchesByHash)
            {
                sha256 = hash();

                if (blocklist.ByHash(sha256) is { } reported)
                {
                    knownBad = true;
                    blocklistReason = reported.Reason;

                    evidence.Insert(0, new SafetyEvidence(
                        SafetySignal.BlocklistFileHash,
                        sha256!,
                        reported.Reason ?? Strings.Current["Core.Safety.Scanner.FileReportFallback"]));
                }
            }

            if (evidence.Count == 0)
                return;

            flagged.Add(new FlaggedFile(filePath, entryPath, sha256 ??= hash(), evidence));
        }

        public SafetyScanResult Build(SafetyTarget target)
        {
            var verdict = knownBad
                ? SafetyVerdict.KnownBad
                : flagged.Count > 0
                    ? SafetyVerdict.Suspicious
                    : SafetyVerdict.NothingFound;

            return new SafetyScanResult(
                target,
                verdict,
                flagged,
                [.. capabilities.Order()],
                [.. unrecognizedHosts],
                unreadable,
                [.. signers],
                signedFiles,
                blocklistReason,
                filesRead);
        }
    }
}
