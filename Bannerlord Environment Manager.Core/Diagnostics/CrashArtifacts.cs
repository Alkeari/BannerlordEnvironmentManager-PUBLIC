using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public sealed record CrashArtifactFolder(string Path, string Label);

public sealed record CapturedArtifact(
    string CapturedPath,
    string OriginalPath,
    string Label,
    long SizeBytes,
    DateTimeOffset CapturedUtc);

public sealed record ArtifactCapture(
    string RunId,
    string Reason,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    string FolderPath,
    IReadOnlyList<CapturedArtifact> Artifacts,
    IReadOnlyList<string> Unreadable,
    // What finishing this capture pruned from the store, written here so the one thing in BEM that
    // deletes anything says what it deleted in the same file it deleted it for.
    string? Retention = null)
{
    public long TotalBytes => Artifacts.Sum(a => a.SizeBytes);
}

// Where the game puts what it writes about a crash, established by decompiling the uploader it ships.
// CrashUploader.Base.UI.Uploader.Cleanup deletes the whole report folder recursively five seconds
// after the upload finishes, so anything BEM wants to read later it has to have copied by then.
public static class CrashArtifactPaths
{
    // Beside BEM's own folder rather than inside it, and deliberately so: the Nexus API key sits at
    // the top of that folder, and a folder a crash scan walks may never sit under a folder holding a
    // credential. NexusKeyLeakTests is the invariant this placement answers to.
    public const string StoreFolderName = "Bannerlord Environment Manager Diagnostics";

    private const string CapturedFolderName = "captured-artifacts";

    // Per version. What a run of one version wrote about its crash is evidence about that version and
    // about nothing else, and one shared store listed a v1.4.8 capture under a freshly downloaded
    // v1.5.2 that had never been launched. The key is InstanceStateFolder's, so there is one answer on
    // the machine to "which folder is this version's", but the folder it names sits out here rather
    // than inside BEM's own: the credential rule above outranks sharing a parent with the other
    // per-version state. The resting version keeps the exact path it has always used, so nothing
    // captured before this change moves or is orphaned.
    public static string GetDefaultRoot(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.KeyFor(dataRoot) is { } key
            ? Path.Combine(StoreRoot(), InstanceStateFolder.InstancesFolderName, key, CapturedFolderName)
            : Path.Combine(StoreRoot(), CapturedFolderName);

    private static string StoreRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        StoreFolderName);

    public static IReadOnlyList<CrashArtifactFolder> DefaultSources(
        string? gameInstallPath, InstanceDataRoot? dataRoot = null)
    {
        var root = dataRoot ?? InstanceDataRoot.ForMachine();
        var shared = root.ProgramData;
        var documents = root.Documents;

        var sources = new List<CrashArtifactFolder>
        {
            new(Path.Combine(shared, "crashes"), "crashes"),
            new(Path.Combine(shared, "logs"), "logs"),
            new(Path.Combine(documents, "crashes"), "butterlib-crashes"),
            new(Path.Combine(documents, "Logs"), "mod-logs"),
            new(Path.Combine(documents, "Configs", "ModLogs"), "butterlib-logs")
        };

        if (!string.IsNullOrWhiteSpace(gameInstallPath))
        {
            var crashDoctor = Path.Combine(gameInstallPath, "Modules", "CrashDoctor");

            sources.Add(new CrashArtifactFolder(Path.Combine(crashDoctor, "managed_reports"), "crashdoctor"));
            sources.Add(new CrashArtifactFolder(Path.Combine(crashDoctor, "cache"), "crashdoctor-cache"));
        }

        return sources;
    }

    // Every process the game and its launchers actually run as, and the uploader that outlives them
    // all. A dry run goes through Bannerlord.BLSE.Standalone.exe, which loads the game into its own
    // process, so nothing on the machine is ever called "Bannerlord" during one: watching only for that
    // name reported the game gone the moment it started, the capture stopped at the end of its warmup,
    // and the uploader deleted the crash folder minutes later with nothing copied out of it.
    private static readonly string[] WatchedProcesses =
    [
        "Bannerlord",
        "Bannerlord.Native",
        "Bannerlord_BE",
        "Bannerlord.BLSE.Standalone",
        "Bannerlord.BLSE.Launcher",
        "Bannerlord.BLSE.LauncherEx",
        "TaleWorlds.MountAndBlade.Launcher",
        "Launcher.Native",
        "CrashUploader.Publish"
    ];

    // The uploader outlives the game: it is a separate process the game starts, and the deletion is
    // the last thing it does. Watching only while the game is alive stops one step too early.
    public static bool GameOrUploaderRunning() => WatchedProcesses.Any(Running);

    public static bool IsWatchedProcess(string? processName) =>
        processName is not null
        && WatchedProcesses.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase));

    private static bool Running(string name)
    {
        try
        {
            var processes = Process.GetProcessesByName(name);

            foreach (var process in processes)
                process.Dispose();

            return processes.Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            return false;
        }
    }
}

// Copies what the game writes about a crash into BEM's own store while the run is going, because the
// game deletes it on the way out. Nothing here ever writes into a game folder or removes anything
// from one: it only reads and copies.
public sealed class CrashArtifactWatcher
{
    private const string ManifestName = "capture.json";

    private readonly string root;
    private readonly string folder;
    private readonly string runId;
    private readonly IReadOnlyList<CrashArtifactFolder> sources;
    private readonly TimeSpan poll;
    private readonly TimeSpan warmup;
    private readonly TimeSpan grace;
    private readonly TimeSpan settle;
    private readonly Func<bool> stillWorking;
    private readonly int keep;
    private readonly int keepDumps;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;

    private string? retention;

    private readonly Dictionary<string, string> captured = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> previous = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CapturedArtifact> artifacts = [];
    private readonly List<string> unreadable = [];

    // A source folder that refuses to be listed, kept apart from the per-file failures so a later sweep
    // that lists it can clear it without wiping the notes about files inside it.
    private readonly Dictionary<string, string> unlistable = new(StringComparer.OrdinalIgnoreCase);

    private bool unlistableChanged;

    public CrashArtifactWatcher(
        string root,
        string runId,
        string reason,
        IReadOnlyList<CrashArtifactFolder> sources,
        TimeSpan? poll = null,
        TimeSpan? warmup = null,
        TimeSpan? grace = null,
        Func<bool>? stillWorking = null,
        TimeSpan? settle = null,
        int? keep = null,
        int? keepDumps = null)
    {
        ArgumentNullException.ThrowIfNull(sources);

        this.runId = runId;
        this.sources = sources;

        // Read from the standing limits rather than baked in as a compile-time default, so the number
        // the user set on the Diagnostic Files page is the number a background capture ages against.
        this.keep = keep ?? ArtifactCaptureRetention.Keep;
        this.keepDumps = keepDumps ?? ArtifactCaptureRetention.KeepDumps;
        this.root = root;

        Reason = reason;
        this.poll = poll ?? TimeSpan.FromSeconds(1);
        this.warmup = warmup ?? TimeSpan.FromSeconds(60);
        this.grace = grace ?? TimeSpan.FromMinutes(5);
        this.settle = settle ?? TimeSpan.FromSeconds(30);
        this.stillWorking = stillWorking ?? CrashArtifactPaths.GameOrUploaderRunning;

        folder = Path.Combine(root, runId);
    }

    public string FolderPath => folder;

    // Settable because the caller learns what the run turned out to be after it has started. The
    // capture has to begin before the game does, so the reason on the manifest is sharpened later.
    public string Reason { get; set; }

    // Canceling the token means the run BEM started has ended, not that the capture should stop:
    // the uploader is still holding the files open and is about to delete them.
    public async Task<ArtifactCapture> WatchAsync(CancellationToken runEnded)
    {
        Directory.CreateDirectory(folder);
        Write(finished: null);

        var clock = Stopwatch.StartNew();

        while (!runEnded.IsCancellationRequested && (clock.Elapsed < warmup || stillWorking()))
        {
            Sweep(settledOnly: true);
            await Wait(runEnded).ConfigureAwait(false);
        }

        var deadline = DateTime.UtcNow + grace;
        var settledBy = DateTime.UtcNow + settle;

        // The settle window runs whether or not anything is detectable right now. A dry run ends the
        // moment the game process is gone, which is before the uploader it started has appeared: asking
        // "is the uploader up?" in that gap answers no, and stopping there loses the dump, the report
        // and the folder they were in, all of which are written afterwards.
        while (DateTime.UtcNow < deadline && (DateTime.UtcNow < settledBy || stillWorking()))
        {
            Sweep(settledOnly: true);
            await Wait(CancellationToken.None).ConfigureAwait(false);
        }

        Sweep(settledOnly: false);

        var finished = DateTimeOffset.UtcNow;

        // The manifest has to say this capture finished before the store is aged, because a capture with
        // no finish time is one retention refuses to touch and this one is the newest thing in there.
        Write(finished);

        var prune = ArtifactCaptureRetention.Prune(root, keep, keepDumps);

        if (prune.RemovedAnything || prune.Failed.Count > 0)
        {
            retention = prune.Describe(keep, keepDumps);
            Write(finished);
        }

        return new ArtifactCapture(runId, Reason, started, finished, folder, [.. artifacts], Unreadable, retention);
    }

    // A folder that could not be listed comes first: it is the one that says the capture does not know
    // what it missed, and a file that could not be copied is at least a named gap.
    private IReadOnlyList<string> Unreadable =>
    [
        .. unlistable.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).Select(entry => entry.Value),
        .. unreadable
    ];

    private async Task Wait(CancellationToken token)
    {
        try
        {
            await Task.Delay(poll, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Sweep(bool settledOnly)
    {
        var changed = false;

        unlistableChanged = false;

        foreach (var source in sources)
        {
            foreach (var (path, signature) in Files(source))
            {
                if (captured.TryGetValue(path, out var already) && already == signature)
                    continue;

                // A file the game is still writing changes between sweeps. Waiting for it to hold
                // still means one copy of the finished file rather than a stream of partial ones,
                // and the final sweep takes whatever is left however recently it changed.
                var settled = previous.TryGetValue(path, out var last) && last == signature;

                previous[path] = signature;

                if (settledOnly && !settled)
                    continue;

                if (Copy(path, source.Label, signature))
                    changed = true;
            }
        }

        if (changed || unlistableChanged)
            Write(finished: null);
    }

    private IEnumerable<(string Path, string Signature)> Files(CrashArtifactFolder source)
    {
        if (string.IsNullOrWhiteSpace(source.Path) || !Directory.Exists(source.Path))
        {
            Listable(source.Path);
            yield break;
        }

        string[] found;

        try
        {
            found = Directory.GetFiles(source.Path, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder BEM could not look inside is not a folder with nothing in it. Treating the two
            // the same is what lets a capture report "found nothing" over evidence it never saw.
            Unlistable(source.Path, $"{source.Path} could not be listed: {ex.Message}");
            yield break;
        }

        Listable(source.Path);

        foreach (var path in found)
        {
            string signature;

            try
            {
                var info = new FileInfo(path);

                // Only what this run produced. Every log the user has ever kept is not evidence of
                // the crash that just happened, and copying all of it would bury the part that is.
                if (info.LastWriteTimeUtc < started.UtcDateTime)
                    continue;

                signature = info.Length + "@" + info.LastWriteTimeUtc.Ticks;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            yield return (path, signature);
        }
    }

    private void Unlistable(string path, string note)
    {
        if (unlistable.TryGetValue(path, out var already) && already == note)
            return;

        unlistable[path] = note;
        unlistableChanged = true;
    }

    private void Listable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !unlistable.Remove(path))
            return;

        unlistableChanged = true;
    }

    private bool Copy(string path, string label, string signature)
    {
        var destination = Path.Combine(folder, label, Relative(path));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // The uploader holds every file in the report open while it sends it, and deletes them
            // the moment it is done, so this asks for the most permissive sharing there is. A copy
            // that fails now is one to try again on the next sweep, never one to give up on.
            using (var input = new FileStream(
                       path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
            }

            captured[path] = signature;
            artifacts.RemoveAll(a => string.Equals(a.OriginalPath, path, StringComparison.OrdinalIgnoreCase));

            artifacts.Add(new CapturedArtifact(
                destination, path, label, new FileInfo(destination).Length, DateTimeOffset.UtcNow));

            unreadable.RemoveAll(u => u.StartsWith(path, StringComparison.OrdinalIgnoreCase));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!captured.ContainsKey(path) && !unreadable.Any(u => u.StartsWith(path, StringComparison.OrdinalIgnoreCase)))
                unreadable.Add($"{path} could not be copied: {ex.Message}");

            return false;
        }
    }

    private static string Relative(string path)
    {
        var root = Path.GetPathRoot(path);

        var trimmed = string.IsNullOrEmpty(root) ? path : path[root.Length..];

        return trimmed.Replace(':', '_').TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void Write(DateTimeOffset? finished)
    {
        try
        {
            var manifest = new ArtifactCapture(
                runId, Reason, started, finished, folder, [.. artifacts], Unreadable, retention);

            var temporary = Path.Combine(folder, ManifestName + ".partial");

            File.WriteAllText(temporary, JsonSerializer.Serialize(manifest), Encoding.UTF8);
            File.Move(temporary, Path.Combine(folder, ManifestName), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed record CaptureRemoval(string FolderPath, int FileCount, long SizeBytes, bool Removed, string Error)
{
    public string Describe() => Removed
        ? Strings.Current.Plural(
            "Core.Diagnostics.CaptureRemoval.Removed", FileCount, ClearedFileStore.DescribeSize(SizeBytes), FolderPath)
        : Strings.Current.Format("Core.Diagnostics.CaptureRemoval.Failed", FolderPath, Error);
}

// Captures are BEM's own copy of evidence that no longer exists anywhere else, so nothing removes one
// on its own: a clear moves it into a batch the Cleared Files list puts back, and Remove ends one only
// because the user named that capture and answered the question. Both are reversible, one from inside
// BEM and one from the Recycle Bin.
public static class ArtifactCaptureStore
{
    private const string ManifestName = "capture.json";

    public static IReadOnlyList<ArtifactCapture> List(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return [];

        var captures = new List<ArtifactCapture>();

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (Read(Path.Combine(folder, ManifestName)) is { } capture)
                captures.Add(capture);
        }

        return [.. captures.OrderByDescending(c => c.StartedUtc)];
    }

    // Every captured file, so a scan can read them beside whatever the game left behind.
    public static IReadOnlyList<CrashArtifactFolder> Folders(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return [];

        return [.. Directory.EnumerateDirectories(root)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => new CrashArtifactFolder(f, Path.GetFileName(f)))];
    }

    // Every file the store holds, manifests included, so what is on disk is what gets measured and what
    // gets offered up to be cleared. Reading the manifests instead would undercount: a dump copied out
    // of a folder the game then deleted is still on disk whether or not its manifest survived, and one
    // crash on this install left 844 MB behind.
    public static IReadOnlyList<ClearCandidate> StoredFiles(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return [];

        try
        {
            return
            [
                .. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Select(path => new ClearCandidate(path, SizeOf(path)))
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // What one capture is holding, so the question the user answers names the size before anything goes.
    public static ClearScope MeasureCapture(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return new ClearScope(0, 0, 0);

        try
        {
            var files = Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories).ToList();

            return new ClearScope(files.Count, files.Sum(SizeOf), 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ClearScope(0, 0, 0);
        }
    }

    // How the folder goes is the caller's, the same way every other removal in Core works: the app hands
    // in the Recycle Bin. A folder outside the store's own root is refused however this was called.
    public static CaptureRemoval Remove(string root, string folderPath, Action<string> removeFolder)
    {
        ArgumentNullException.ThrowIfNull(removeFolder);

        var scope = MeasureCapture(folderPath);

        if (!InsideRoot(root, folderPath))
        {
            return new CaptureRemoval(
                folderPath, 0, 0, false, Strings.Current["Core.Diagnostics.CaptureRemoval.OutsideRoot"]);
        }

        if (!Directory.Exists(folderPath))
            return new CaptureRemoval(folderPath, 0, 0, false, Strings.Current["Core.Diagnostics.CaptureRemoval.Gone"]);

        try
        {
            removeFolder(folderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CaptureRemoval(folderPath, scope.FileCount, scope.SizeBytes, false, ex.Message);
        }

        // A Recycle Bin that declined leaves the folder where it was, and reporting that as a success is
        // the same defect as failing silently.
        return Directory.Exists(folderPath)
            ? new CaptureRemoval(
                folderPath, scope.FileCount, scope.SizeBytes, false, Strings.Current["Core.Diagnostics.CaptureRemoval.StillOnDisk"])
            : new CaptureRemoval(folderPath, scope.FileCount, scope.SizeBytes, true, string.Empty);
    }

    private static bool InsideRoot(string root, string folderPath)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(folderPath))
            return false;

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        var rooted = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        return full.Length > rooted.Length
            && full.StartsWith(rooted + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static ArtifactCapture? Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ArtifactCapture>(File.ReadAllText(path))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

public sealed record ArtifactCapturePrune(
    IReadOnlyList<string> RemovedCaptures,
    long CaptureBytes,
    int RemovedDumps,
    long DumpBytes,
    IReadOnlyList<string> Failed)
{
    public static ArtifactCapturePrune Nothing { get; } = new([], 0, 0, 0, []);

    public bool RemovedAnything => RemovedCaptures.Count > 0 || RemovedDumps > 0;

    public string Describe(int keep, int keepDumps)
    {
        var parts = new List<string>();

        if (RemovedCaptures.Count > 0)
        {
            parts.Add(Strings.Current.Plural(
                "Core.Diagnostics.CapturePrune.DeletedCaptures",
                RemovedCaptures.Count,
                keep,
                ClearedFileStore.DescribeSize(CaptureBytes),
                string.Join(", ", RemovedCaptures)));
        }

        if (RemovedDumps > 0)
        {
            parts.Add(Strings.Current.Plural(
                "Core.Diagnostics.CapturePrune.DeletedDumps",
                RemovedDumps,
                keepDumps,
                ClearedFileStore.DescribeSize(DumpBytes)));
        }

        if (Failed.Count > 0)
        {
            parts.Add(Strings.Current.Format(
                "Core.Diagnostics.CapturePrune.Failed", Failed.Count, string.Join("; ", Failed)));
        }

        return parts.Count == 0
            ? Strings.Current.Format("Core.Diagnostics.CapturePrune.Nothing", keep, keepDumps)
            : string.Join(" ", parts);
    }

    // retention-log.txt is a log, not UI: its readership is whoever diagnoses what BEM pruned, on
    // whatever machine that turns out to be, so this stays English regardless of the app's language.
    public string DescribeForLog(int keep, int keepDumps)
    {
        var parts = new List<string>();

        if (RemovedCaptures.Count > 0)
        {
            parts.Add(
                $"Deleted {RemovedCaptures.Count} capture(s) older than the {keep} most recent, " +
                $"freeing {ClearedFileStore.DescribeSize(CaptureBytes)}: {string.Join(", ", RemovedCaptures)}.");
        }

        if (RemovedDumps > 0)
        {
            parts.Add(
                $"Deleted {RemovedDumps} crash dump(s) from captures outside the {keepDumps} most recent, " +
                $"freeing {ClearedFileStore.DescribeSize(DumpBytes)}. Every text artifact beside them was kept.");
        }

        if (Failed.Count > 0)
            parts.Add($"{Failed.Count} could not be deleted and are still there: {string.Join("; ", Failed)}.");

        return parts.Count == 0
            ? $"Nothing was old enough to delete. The {keep} most recent captures are kept whole, and dumps are kept for the {keepDumps} most recent."
            : string.Join(" ", parts);
    }
}

// The one place in BEM that deletes something the user did not ask it to delete, so every rule here is
// written to be defended rather than assumed.
//
// A capture holds two very different things. The text - crash reports, logs, ButterLib's own records -
// is small, and CrashReportLocator parses .txt and .html. A .dmp is large, often the whole size of a
// capture. So the two are aged out on separate clocks: captures are kept whole for a long time, and
// dumps for a short one.
//
// That split used to rest on BEM having no reader for a dump at all, which is no longer true: CrashDumps
// reads one and puts the exception and its stack on the Crash Reports tab. What keeps the split honest
// is where it reads them. Windows Error Reporting's own folder is not a capture, nothing deletes from
// it, and BEM never writes to it, so pruning a captured dump still cannot destroy the evidence behind
// a verdict BEM produced. If a verdict is ever built from a dump inside a capture, this clock is wrong
// and has to move.
//
// Whole captures are what could destroy an unread verdict, and BEM does not record which crash reports
// the user has read, so it cannot know. Recency is the only proxy that exists, which is exactly why
// the whole-capture limit is set generously rather than tightly, and why an unfinished capture is
// never touched at any age.
public static class ArtifactCaptureRetention
{
    public const int DefaultKeep = 50;

    public const int DefaultKeepDumps = 3;

    public const string LogFileName = "retention-log.txt";

    private const string DumpExtension = ".dmp";

    // The limits a capture ages itself against when the caller names none. They are generous on purpose
    // and are the user's to change: a background prune that outlived the number they typed would be
    // deleting evidence they asked to keep.
    public static int Keep { get; private set; } = DefaultKeep;

    public static int KeepDumps { get; private set; } = DefaultKeepDumps;

    public static void Use(int keep, int keepDumps)
    {
        Keep = Math.Max(1, keep);
        KeepDumps = Math.Max(0, keepDumps);
    }

    // A cleared number box is not a number. It arrives as NaN, casting that to an int lands on
    // int.MinValue, and clamping int.MinValue up to the minimum turns one stray backspace into "keep
    // the newest capture and delete the rest". Captures are the only surviving copy of what the game
    // deleted, so an unreadable box reads as nothing and the limit already in force stays.
    public static bool TryReadLimit(double typed, int minimum, out int limit)
    {
        limit = minimum;

        if (double.IsNaN(typed) || double.IsInfinity(typed) || typed < minimum || typed > int.MaxValue)
            return false;

        limit = (int)Math.Round(typed);

        return true;
    }

    // A refused edit leaves the box showing whatever the user left in it, which for a cleared box is
    // nothing at all: the limit is unchanged behind it and invisible in front of it, and a limit nobody
    // can read is a limit nobody can trust. This says what the box has to show. Null means what is
    // already there reads back as the number in force, so nothing needs repainting.
    public static string? LimitTextToShow(string? shown, int limit) =>
        double.TryParse(shown, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var read)
        && !double.IsNaN(read)
        && read == limit
            ? null
            : limit.ToString(CultureInfo.CurrentCulture);

    public static string Describe(string root, int keep = DefaultKeep, int keepDumps = DefaultKeepDumps)
    {
        var files = ArtifactCaptureStore.StoredFiles(root);
        var dumps = files.Where(IsDump).ToList();

        if (files.Count == 0)
            return Strings.Current.Format("Core.Diagnostics.CaptureRetention.NoneYet", keep);

        return Strings.Current.Plural(
            "Core.Diagnostics.CaptureRetention.Describe",
            files.Count,
            ClearedFileStore.DescribeSize(files.Sum(f => f.SizeBytes)),
            root,
            dumps.Count,
            ClearedFileStore.DescribeSize(dumps.Sum(f => f.SizeBytes)),
            keep,
            keepDumps);
    }

    // How a folder and a file go is delegated so Core stays free of Windows. A prune the user asked for
    // by hand hands in the Recycle Bin; the background prune at the end of a capture hands in nothing and
    // gets the plain delete, because a dump moved to the Recycle Bin has not given the disk space back.
    public static ArtifactCapturePrune Prune(
        string root,
        int keep = DefaultKeep,
        int keepDumps = DefaultKeepDumps,
        Action<string>? removeFolder = null,
        Action<string>? removeFile = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || keep < 1 || keepDumps < 0)
            return ArtifactCapturePrune.Nothing;

        var ordered = Ordered(root);
        var known = ArtifactCaptureStore.List(root);

        var removed = new List<string>();
        var failed = new List<string>();
        var captureBytes = 0L;

        foreach (var folder in ordered.Skip(keep))
        {
            // A capture with no readable manifest, or one whose manifest has no finish time, is either
            // still going or is the wreckage of a run that never got to write one. Neither is something
            // to delete on age.
            if (known.FirstOrDefault(capture => PathsMatch(capture.FolderPath, folder)) is not { FinishedUtc: not null })
                continue;

            var size = Size(folder);

            try
            {
                if (removeFolder is null)
                    Directory.Delete(folder, recursive: true);
                else
                    removeFolder(folder);

                removed.Add(Path.GetFileName(folder));
                captureBytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{Path.GetFileName(folder)}: {ex.Message}");
            }
        }

        var dumps = 0;
        var dumpBytes = 0L;

        foreach (var folder in ordered.Skip(keepDumps).Where(Directory.Exists))
        {
            foreach (var dump in Dumps(folder))
            {
                var size = SizeOf(dump);

                try
                {
                    if (removeFile is null)
                        File.Delete(dump);
                    else
                        removeFile(dump);

                    dumps++;
                    dumpBytes += size;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add($"{Path.GetFileName(dump)}: {ex.Message}");
                }
            }
        }

        var prune = new ArtifactCapturePrune(removed, captureBytes, dumps, dumpBytes, failed);

        if (prune.RemovedAnything || failed.Count > 0)
            Log(root, prune.DescribeForLog(keep, keepDumps));

        return prune;
    }

    // Newest first, by the folder name the capture was given, which starts with a sortable timestamp.
    // The manifest is not used to order because a capture whose manifest never got written still has to
    // take its right place rather than drift to one end of the list.
    private static IReadOnlyList<string> Ordered(string root)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(root).OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> Dumps(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFiles(folder, "*" + DumpExtension, SearchOption.AllDirectories)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsDump(ClearCandidate file) =>
        Path.GetExtension(file.Path).Equals(DumpExtension, StringComparison.OrdinalIgnoreCase);

    private static bool PathsMatch(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            StringComparison.OrdinalIgnoreCase);

    private static long Size(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(SizeOf);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // Beside the captures rather than inside one, so the record of a deletion outlives whatever was
    // being captured when it happened.
    private static void Log(string root, string line)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(root, LogFileName),
                $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  {line}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
