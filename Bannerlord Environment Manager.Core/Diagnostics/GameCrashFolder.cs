using System.Globalization;
using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// One line out of crash_tags.txt, which the game writes as [Section][Key][Value] and nothing else.
public sealed record GameCrashTag(string Section, string Key, string Value);

// One frame out of stack.txt. The game writes native frames as a pdb name and a byte offset, so a
// frame names a binary and never a method. A frame it could not resolve is kept rather than dropped,
// for the same reason a dump's unresolved frames are: a hole in a stack is why an answer stops short.
public sealed record NativeFrame(string Module, string Offset)
{
    public bool IsResolved => Module.Length > 0;

    public string Describe() => IsResolved
        ? Offset.Length > 0 ? $"{Module} + {Offset}" : Module
        : Strings.Current["Core.Diagnostics.GameCrashFolder.UnresolvedFrame"];
}

// One line BEM could attribute to a module that was in this run's launch order. Position is that
// module's place on the command line the game was started with, which is the order its load step runs
// in.
public sealed record ModuleWrite(
    string ModuleId,
    int Position,
    DateTimeOffset When,
    string Line,
    string Source);

// How far through its load order the run demonstrably got. This is the single derivation that turns a
// pile of artifacts into an answer, and it is also the one most able to overclaim, so what it asserts
// and what it merely fails to observe are kept strictly apart:
//
//   - A module that wrote anything had been reached. Everything at or before its position had its load
//     step entered, because the game runs them in the order they appear on the command line. That half
//     is a fact.
//   - A module that wrote nothing may still have loaded silently. So the modules after that position
//     are candidates, never culprits, and the gap between the last thing anything said and the fault is
//     stated so the reader can see how much room there is for a silent load.
//
// Nothing here names a mod.
public sealed record InitializationBoundary(
    ModuleWrite Furthest,
    ModuleWrite Latest,
    int TotalModules,
    DateTimeOffset? FaultAt,
    IReadOnlyList<string> HadInitialized,
    IReadOnlyList<string> NotObserved)
{
    // How long nothing said anything before the process died. The bigger this is, the more room there
    // was for a module to load without logging, and the weaker the candidate list gets.
    public TimeSpan? Silence => FaultAt is { } fault && fault >= Latest.When ? fault - Latest.When : null;

    // The firm half. A module at or before the boundary had its load step entered, so it cannot be the
    // thing that had not loaded yet.
    public bool Clears(string moduleId) =>
        HadInitialized.Contains(moduleId, StringComparer.OrdinalIgnoreCase);

    // The guard against BEM contradicting itself. A static check that names a module the load order
    // shows already running must not be left reading as the cause of this crash.
    public string? Contradicts(string moduleId)
    {
        if (!Clears(moduleId))
            return null;

        var position = HadInitialized
            .ToList()
            .FindIndex(id => string.Equals(id, moduleId, StringComparison.OrdinalIgnoreCase)) + 1;

        return Strings.Current.Format(
            "Core.Diagnostics.GameCrashFolder.Boundary.Contradicts",
            moduleId,
            position.ToString(CultureInfo.InvariantCulture),
            TotalModules.ToString(CultureInfo.InvariantCulture),
            Furthest.ModuleId,
            Furthest.Position.ToString(CultureInfo.InvariantCulture));
    }

    public string Describe()
    {
        var lines = new List<string>
        {
            Strings.Current.Format(
                "Core.Diagnostics.GameCrashFolder.Boundary.Furthest",
                Furthest.ModuleId,
                Furthest.Position.ToString(CultureInfo.InvariantCulture),
                TotalModules.ToString(CultureInfo.InvariantCulture),
                Furthest.When.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)),

            Strings.Current.Plural(
                "Core.Diagnostics.GameCrashFolder.Boundary.HadInitialized", HadInitialized.Count),

            Strings.Current.Plural(
                "Core.Diagnostics.GameCrashFolder.Boundary.NotObserved", NotObserved.Count)
        };

        if (!string.Equals(Latest.ModuleId, Furthest.ModuleId, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add(Strings.Current.Format(
                "Core.Diagnostics.GameCrashFolder.Boundary.Latest",
                Latest.ModuleId,
                Latest.When.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)));
        }

        if (Silence is { } quiet)
        {
            lines.Add(Strings.Current.Plural(
                "Core.Diagnostics.GameCrashFolder.Boundary.Silence",
                (long)Math.Round(quiet.TotalSeconds),
                quiet.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)));
        }

        return string.Join(" ", lines);
    }
}

// One crash folder the game wrote, read as one crash report. The game does not write a crash report as
// a file: it writes a dated folder of artifacts, and BEM read that folder file by file, found that no
// single file parsed as a managed exception trace, and reported that it had found no crash report at
// all while a complete one sat on disk.
//
// Everything here is read. Nothing in this file writes to, moves or deletes anything in a crash folder.
public sealed record GameCrashFolderReading(
    string Path,
    DateTimeOffset Written,
    int ProcessId,
    WindowsFaultCode? Code,
    string FaultAddress,
    DateTimeOffset? FaultAt,
    TimeSpan? RunTime,
    IReadOnlyList<string> LaunchOrder,
    IReadOnlyList<GameCrashTag> Tags,
    IReadOnlyList<NativeFrame> Stack,
    InitializationBoundary? Boundary,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Unreadable)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string? DumpPath => Files.FirstOrDefault(file =>
        System.IO.Path.GetExtension(file).Equals(".dmp", StringComparison.OrdinalIgnoreCase));

    public string? GameLogPath => Files.FirstOrDefault(file =>
        System.IO.Path.GetFileName(file).StartsWith("rgl_log_", StringComparison.OrdinalIgnoreCase)
        && !System.IO.Path.GetFileName(file).StartsWith("rgl_log_errors", StringComparison.OrdinalIgnoreCase));

    public string? ErrorLogPath => Files.FirstOrDefault(file =>
        System.IO.Path.GetFileName(file).StartsWith("rgl_log_errors", StringComparison.OrdinalIgnoreCase));

    public string Value(string section, string key) => Tags
        .FirstOrDefault(tag =>
            string.Equals(tag.Section, section, StringComparison.OrdinalIgnoreCase)
            && string.Equals(tag.Key, key, StringComparison.OrdinalIgnoreCase))
        ?.Value ?? string.Empty;

    public string Headline() => Code is { } code
        ? Strings.Current.Format("Core.Diagnostics.GameCrashFolder.Headline.WithCode", code.Hex, FaultAddress)
        : Strings.Current["Core.Diagnostics.GameCrashFolder.Headline.NoCode"];

    // The sentence this exists to be able to say, and the one it must never exceed.
    public static string NamesNoMod => Strings.Current["Core.Diagnostics.GameCrashFolder.NamesNoMod"];

    public string Describe()
    {
        var lines = new List<string> { Headline() };

        if (FaultAt is { } fault)
        {
            lines.Add(Strings.Current.Format(
                "Core.Diagnostics.GameCrashFolder.FaultAt",
                fault.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)));
        }

        if (RunTime is { } ran)
        {
            lines.Add(Strings.Current.Plural(
                "Core.Diagnostics.GameCrashFolder.RunTime",
                (long)Math.Round(ran.TotalSeconds),
                ran.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)));
        }

        if (ProcessId > 0)
            lines.Add(Strings.Current.Format("Core.Diagnostics.GameCrashFolder.ProcessId", ProcessId.ToString(CultureInfo.InvariantCulture)));

        if (LaunchOrder.Count > 0)
        {
            lines.Add(Strings.Current.Plural(
                "Core.Diagnostics.GameCrashFolder.LaunchOrderCount", LaunchOrder.Count));
        }

        if (Boundary is { } boundary)
            lines.Add(boundary.Describe());
        else if (LaunchOrder.Count > 0)
        {
            lines.Add(Strings.Current["Core.Diagnostics.GameCrashFolder.NoBoundaryAttribution"]);
        }

        lines.Add(NamesNoMod);

        return string.Join(" ", lines);
    }
}

public sealed record GameCrashFolderSearch(
    IReadOnlyList<GameCrashFolderReading> Folders,
    IReadOnlyList<string> Searched,
    IReadOnlyList<string> Unreadable)
{
    public string Describe()
    {
        if (Folders.Count > 0)
        {
            return Strings.Current.Plural(
                "Core.Diagnostics.GameCrashFolder.Search.Read",
                Folders.Count,
                Folders[0].Written.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        return Unreadable.Count == 0
            ? Strings.Current["Core.Diagnostics.GameCrashFolder.Search.NoneFound"]
            : Strings.Current.Format(
                "Core.Diagnostics.GameCrashFolder.Search.RootUnreadable", string.Join("; ", Unreadable));
    }
}

// Finds and reads the game's own crash folders. The shape below was established by reading a real one
// on this machine rather than from documentation: a dated folder holding crash_tags.txt, stack.txt,
// module_list.txt, dump.dmp, rgl_log_<pid>.txt, rgl_log_errors_<pid>.txt and every mod log the game
// happened to capture beside them.
public static partial class GameCrashFolders
{
    private const int MaxLines = 200000;
    private const int MaxLineLength = 500;

    // Any one of these makes a folder a crash folder. All three together is the usual case; a run that
    // died before the tags were written still leaves the error log, and that is still a crash.
    private static readonly string[] Markers = ["crash_tags.txt", "stack.txt"];

    // Files in a crash folder that are settings or inventories rather than anything anybody logged.
    // Reading them for a timestamped line would attribute a config file's contents to a module.
    private static readonly string[] NotLogs =
    [
        "crash_tags.txt", "module_list.txt", "stack.txt", "BannerlordConfig.txt",
        "BannerlordConfig.txt.bak", "engine_config.txt", "engine_config.txt.bak", "user_config.txt",
        "settings.json", "Options.json", "config.xml", "LauncherData.xml", "BannerlordGameKeys.xml"
    ];

    private static readonly string[] LogExtensions = [".log", ".txt"];

    // The game's own words, typo included. "adress" is what it writes, and correcting it here would
    // stop this matching the only file it exists to read. Both spellings are accepted so that a future
    // build fixing it does not silently break this.
    [GeneratedRegex(@"Unhandled\s+Exception\s+Code\s+0x(?<code>[0-9A-Fa-f]+)\s+at\s+add?ress\s+0x(?<address>[0-9A-Fa-f]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex UnhandledException { get; }

    [GeneratedRegex(@"^\[(?<section>[^\]]*)\]\[(?<key>[^\]]*)\]\[(?<value>.*)\]\s*$")]
    private static partial Regex TagLine { get; }

    // A native frame as stack.txt writes it: a pdb name, a signature, and a byte offset.
    [GeneratedRegex(@"^#(?<module>[^@]+?)\.pdb@\{[^}]*\}\s*\([^)]*\):\s*\d+:(?<offset>\d+)")]
    private static partial Regex StackFrame { get; }

    [GeneratedRegex(@"^rgl_log(?:_errors)?_(?<pid>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessIdInName { get; }

    [GeneratedRegex(@"^\[(?<stamp>[^\]]{7,40})\]")]
    private static partial Regex BracketedStamp { get; }

    [GeneratedRegex(@"^(?<stamp>\d{1,2}:\d{2}:\d{2}(?:[.,]\d+)?)\b")]
    private static partial Regex BareTime { get; }

    // Who wrote a line, when the writer tags itself. [RCM] is what put a number on this crash.
    [GeneratedRegex(@"\[(?<owner>[^\[\]]{1,64})\]")]
    private static partial Regex BracketedOwner { get; }

    // Trailing dates and rotation indexes a logger puts on its own file name, which are the logger's
    // doing and not part of the module's name.
    [GeneratedRegex(@"[._-]?\d{4,8}$")]
    private static partial Regex StampedName { get; }

    public static IReadOnlyList<string> DefaultRoots(string? gameInstallPath, InstanceDataRoot? dataRoot = null)
    {
        var roots = new List<string>
        {
            Path.Combine((dataRoot ?? InstanceDataRoot.ForMachine()).ProgramData, "crashes")
        };

        if (!string.IsNullOrWhiteSpace(gameInstallPath))
            roots.Add(Path.Combine(gameInstallPath, "Modules", "CrashDoctor", "cache"));

        return roots;
    }

    public static bool IsCrashFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return false;

        try
        {
            if (Markers.Any(marker => File.Exists(Path.Combine(folder, marker))))
                return true;

            return Directory.EnumerateFiles(folder, "rgl_log_errors_*.txt", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static GameCrashFolderSearch Find(string? gameInstallPath, InstanceDataRoot? dataRoot = null) =>
        Find(DefaultRoots(gameInstallPath, dataRoot));

    public static GameCrashFolderSearch Find(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var readings = new List<GameCrashFolderReading>();
        var searched = new List<string>();
        var unreadable = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            searched.Add(root);

            if (!Directory.Exists(root))
                continue;

            string[] children;

            try
            {
                children = Directory.GetDirectories(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder BEM could not look inside is not a folder with nothing in it.
                unreadable.Add($"{root} could not be listed: {ex.Message}");
                continue;
            }

            // The root itself can be the crash folder on an install that redirects it.
            foreach (var folder in children.Append(root))
            {
                if (!IsCrashFolder(folder) || !seen.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))))
                    continue;

                try
                {
                    readings.Add(Read(folder));
                }
                // Deliberately broad. A crash folder is written by a game that had just failed, by
                // whatever mod loggers happened to be loaded, and one folder BEM cannot parse must
                // never cost the user the folder beside it. A real offset in this install's stack.txt
                // was ulong.MaxValue, which is the sort of thing this catch exists for.
                catch (Exception ex)
                {
                    unreadable.Add($"{folder} could not be read: {ex.Message}");
                }
            }
        }

        return new GameCrashFolderSearch(
            [.. readings.OrderByDescending(reading => reading.Written)],
            searched,
            unreadable);
    }

    public static GameCrashFolderReading Read(string folder)
    {
        var unreadable = new List<string>();
        var files = Files(folder, unreadable);

        var written = Written(folder);
        var localDate = DateOnly.FromDateTime(written.ToLocalTime().DateTime);

        var tags = Tags(files, unreadable);
        var launchOrder = LaunchOrder(tags);
        var stack = Stack(files, unreadable);

        var (code, address, faultAt, processId) = Fault(files, localDate, unreadable);

        return new GameCrashFolderReading(
            folder,
            written,
            processId,
            code,
            address,
            faultAt,
            RunTime(tags),
            launchOrder,
            tags,
            stack,
            Boundary(files, launchOrder, faultAt, localDate, unreadable),
            files,
            unreadable);
    }

    private static DateTimeOffset Written(string folder)
    {
        try
        {
            return new DateTimeOffset(Directory.GetLastWriteTimeUtc(folder), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static IReadOnlyList<string> Files(string folder, List<string> unreadable)
    {
        try
        {
            return [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable.Add($"{folder} could not be listed: {ex.Message}");

            return [];
        }
    }

    private static IReadOnlyList<GameCrashTag> Tags(IReadOnlyList<string> files, List<string> unreadable)
    {
        var path = files.FirstOrDefault(file =>
            Path.GetFileName(file).Equals("crash_tags.txt", StringComparison.OrdinalIgnoreCase));

        if (path is null)
            return [];

        var tags = new List<GameCrashTag>();

        foreach (var line in Lines(path, unreadable))
        {
            if (TagLine.Match(line) is { Success: true } tag)
            {
                tags.Add(new GameCrashTag(
                    tag.Groups["section"].Value,
                    tag.Groups["key"].Value,
                    tag.Groups["value"].Value));
            }
        }

        return tags;
    }

    // The command line the game was actually started with, which is the load order that ran. Read from
    // the crash folder rather than from the launcher's settings, so it is what happened rather than
    // what is configured now.
    private static IReadOnlyList<string> LaunchOrder(IReadOnlyList<GameCrashTag> tags)
    {
        var arguments = tags
            .FirstOrDefault(tag =>
                tag.Section.Equals("Runtime", StringComparison.OrdinalIgnoreCase)
                && tag.Key.Equals("Arguments", StringComparison.OrdinalIgnoreCase))
            ?.Value ?? string.Empty;

        const string Marker = "_MODULES_";

        var start = arguments.IndexOf(Marker, StringComparison.Ordinal);
        var end = arguments.LastIndexOf(Marker, StringComparison.Ordinal);

        if (start < 0 || end <= start)
            return [];

        return
        [
            .. arguments[(start + Marker.Length)..end]
                .Split('*', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ];
    }

    private static TimeSpan? RunTime(IReadOnlyList<GameCrashTag> tags)
    {
        var value = tags
            .FirstOrDefault(tag =>
                tag.Section.Equals("Runtime", StringComparison.OrdinalIgnoreCase)
                && tag.Key.Equals("App Run Time", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
               && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    private static IReadOnlyList<NativeFrame> Stack(IReadOnlyList<string> files, List<string> unreadable)
    {
        var path = files.FirstOrDefault(file =>
            Path.GetFileName(file).Equals("stack.txt", StringComparison.OrdinalIgnoreCase));

        if (path is null)
            return [];

        var frames = new List<NativeFrame>();

        foreach (var line in Lines(path, unreadable))
        {
            if (StackFrame.Match(line) is { Success: true } frame)
            {
                // Parsed as unsigned and never assumed to fit. A real frame in this install's own
                // stack.txt carries 18446744073709551615, which is what the game writes when it has no
                // offset at all, and reading that as a signed number threw and took the whole folder
                // with it.
                var offset = ulong.TryParse(
                    frame.Groups["offset"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                    ? "0x" + value.ToString("X", CultureInfo.InvariantCulture)
                    : string.Empty;

                frames.Add(new NativeFrame(Path.GetFileName(frame.Groups["module"].Value), offset));

                continue;
            }

            if (line.StartsWith("@unknown_module", StringComparison.OrdinalIgnoreCase))
                frames.Add(new NativeFrame(string.Empty, string.Empty));
        }

        return frames;
    }

    // The exception code and the address, out of the engine's own error log. This is the whole reason
    // that file is not a settings file: it carries the fault in one line of plain text, and BEM was
    // throwing it away for failing to look like a managed stack trace.
    private static (WindowsFaultCode? Code, string Address, DateTimeOffset? At, int ProcessId) Fault(
        IReadOnlyList<string> files,
        DateOnly localDate,
        List<string> unreadable)
    {
        var path = files.FirstOrDefault(file =>
            Path.GetFileName(file).StartsWith("rgl_log_errors", StringComparison.OrdinalIgnoreCase));

        if (path is null)
            return (null, string.Empty, null, 0);

        var processId = ProcessIdOf(path);

        DateTimeOffset? stamp = null;

        foreach (var line in Lines(path, unreadable))
        {
            if (Timestamp(line, localDate) is { } at)
                stamp = at;

            if (UnhandledException.Match(line) is not { Success: true } fault)
                continue;

            var code = WindowsFaultCodes.Parse(fault.Groups["code"].Value);

            return (
                code is { } value ? WindowsFaultCodes.Translate(value) : null,
                "0x" + fault.Groups["address"].Value.ToUpperInvariant(),
                stamp,
                processId);
        }

        return (null, string.Empty, null, processId);
    }

    public static int ProcessIdOf(string path)
    {
        var match = ProcessIdInName.Match(Path.GetFileNameWithoutExtension(path));

        return match.Success
            && int.TryParse(match.Groups["pid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                ? id
                : 0;
    }

    // The derivation. Every log in the folder is read for lines BEM can attribute to a module that was
    // in this run's launch order, and the furthest one through that order is the boundary.
    private static InitializationBoundary? Boundary(
        IReadOnlyList<string> files,
        IReadOnlyList<string> launchOrder,
        DateTimeOffset? faultAt,
        DateOnly localDate,
        List<string> unreadable)
    {
        if (launchOrder.Count == 0)
            return null;

        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < launchOrder.Count; index++)
            positions.TryAdd(launchOrder[index], index + 1);

        ModuleWrite? furthest = null;
        ModuleWrite? latest = null;

        foreach (var file in files.Where(IsLog))
        {
            // The file's own name is a writer too, and a weaker one than a tag inside the line, so it
            // is only used when the line names nobody.
            var fromName = ModuleFromName(file, positions);

            DateTimeOffset? stamp = null;

            foreach (var raw in Lines(file, unreadable))
            {
                var line = raw.Length <= MaxLineLength ? raw : raw[..MaxLineLength] + " ...";

                if (Timestamp(line, localDate) is { } at)
                    stamp = at;

                if (stamp is not { } when)
                    continue;

                // Only what happened before the process died. A line written afterwards belongs to the
                // uploader, not to the run.
                if (faultAt is { } fault && when > fault)
                    continue;

                var owner = ModuleFromLine(line, positions) ?? fromName;

                if (owner is not { } named)
                    continue;

                var write = new ModuleWrite(named, positions[named], when, line.Trim(), file);

                if (furthest is null || write.Position > furthest.Position)
                    furthest = write;

                if (latest is null || write.When > latest.When)
                    latest = write;
            }
        }

        if (furthest is null || latest is null)
            return null;

        return new InitializationBoundary(
            furthest,
            latest,
            launchOrder.Count,
            faultAt,
            [.. launchOrder.Take(furthest.Position)],
            [.. launchOrder.Skip(furthest.Position)]);
    }

    private static bool IsLog(string path)
    {
        var name = Path.GetFileName(path);

        return LogExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            && !NotLogs.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    // Exact matches only, and deliberately so. A module named by a tag inside a line is evidence; a
    // module guessed at from a partial name is how the wrong mod ends up in a sentence about a crash.
    private static string? ModuleFromLine(string line, Dictionary<string, int> positions)
    {
        foreach (Match match in BracketedOwner.Matches(line))
        {
            var token = match.Groups["owner"].Value.Trim();

            if (token.Length == 0 || positions.ContainsKey(token) is false)
                continue;

            return positions.Keys.First(id => string.Equals(id, token, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static string? ModuleFromName(string path, Dictionary<string, int> positions)
    {
        var stem = Path.GetFileNameWithoutExtension(path);

        foreach (var candidate in new[] { stem, StampedName.Replace(stem, string.Empty) })
        {
            if (candidate.Length > 0 && positions.ContainsKey(candidate))
                return positions.Keys.First(id => string.Equals(id, candidate, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static IEnumerable<string> Lines(string path, List<string> unreadable)
    {
        StreamReader reader;

        try
        {
            reader = new StreamReader(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            unreadable.Add($"{path} could not be read: {ex.Message}");

            yield break;
        }

        using (reader)
        {
            for (var number = 0; number < MaxLines; number++)
            {
                string? line;

                try
                {
                    line = reader.ReadLine();
                }
                catch (Exception ex) when (ex is IOException or OutOfMemoryException)
                {
                    unreadable.Add($"{path} could not be read past line {number}: {ex.Message}");

                    yield break;
                }

                if (line is null)
                    yield break;

                yield return line;
            }
        }
    }

    // Mod loggers in one crash folder stamp lines five different ways, so the parse tries the shapes
    // that actually appear rather than one format. A line with no readable stamp is not a timestamp and
    // contributes nothing rather than a guess.
    private static DateTimeOffset? Timestamp(string line, DateOnly localDate)
    {
        if (BracketedStamp.Match(line) is { Success: true } bracketed
            && Parse(bracketed.Groups["stamp"].Value, localDate) is { } inBrackets)
        {
            return inBrackets;
        }

        return BareTime.Match(line) is { Success: true } bare
            ? Parse(bare.Groups["stamp"].Value, localDate)
            : null;
    }

    private static DateTimeOffset? Parse(string text, DateOnly localDate)
    {
        var trimmed = text.Trim().Replace(',', '.');

        if (trimmed.Contains('-', StringComparison.Ordinal) || trimmed.Contains('/', StringComparison.Ordinal))
        {
            return DateTimeOffset.TryParse(
                trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var offset)
                ? offset
                : null;
        }

        if (!TimeOnly.TryParse(trimmed, CultureInfo.InvariantCulture, out var time))
            return null;

        var moment = localDate.ToDateTime(time);

        return new DateTimeOffset(moment, TimeZoneInfo.Local.GetUtcOffset(moment));
    }
}
