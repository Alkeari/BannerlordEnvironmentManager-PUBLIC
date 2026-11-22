using System.IO.Compression;
using System.Net;
using System.Text;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Modules.KnownIssues;

namespace BannerlordEnvironmentManager.Core.DryRun;

// Everything the Bannerlord Exception Manager's window shows about one caught crash, as rows of text, and
// the same thing as a report the player can copy or save. Built from the Companion's record and the game
// folder it names; nothing here needs the rest of BEM to be running, since the window opens in a process
// of its own while the game waits on the answer.
public sealed record ExceptionReport(
    CrashGuardRecord Record,
    string Headline,
    string Notice,
    IReadOnlyList<string> Suspects,
    IReadOnlyList<string> SuspectIds,
    IReadOnlyList<string> Frames,
    IReadOnlyList<string> Mods,
    IReadOnlyList<string> Assemblies,
    IReadOnlyList<string> Patches)
{
    // The game build the crash happened on, read from the game folder; empty when that folder is not there.
    public string GameVersion { get; init; } = string.Empty;

    // What the record alone says, with no disk read: shown at once, while Build looks at the mods.
    public static ExceptionReport FromRecord(CrashGuardRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var attribution = CrashAttributor.Attribute(record.Stack, record.PatchOwners);

        return Compose(record, attribution, manifests: null, duplicates: []);
    }

    // Reads the mods in the game folder the record names, so a frame inside a mod's own code is named by
    // its mod and a patch the game did not record an owner for is still found. Slower by a disk scan.
    public static ExceptionReport Build(CrashGuardRecord record, EngineLogFindings? engineLog = null)
    {
        ArgumentNullException.ThrowIfNull(record);

        var install = record.GamePath ?? string.Empty;

        if (install.Length == 0 || !Directory.Exists(install))
            return FromRecord(record);

        var scan = ModuleScanner.ScanAll(install);

        if (scan.Failed)
            return FromRecord(record);

        // Two folders declaring one module id is an error in the install: the game loads only one of them, and
        // which one is not the player's choice. The report names it; the first folder stands in for its rows.
        var manifests = new Dictionary<string, ModuleManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in scan.Modules)
            manifests.TryAdd(m.Id.Value, m);
        var duplicates = DuplicateIds(scan.Modules);
        var entries = ModuleEnvironment.Merge(
            scan, [.. (record.Modules ?? []).Select(id => new LauncherModEntry(new ModuleId(id), true))]).Entries;

        var index = AssemblyIndex.Build(install);
        var overlaps = index.Failed ? [] : HarmonyPatchOverlaps.Inspect(index, entries).Overlaps;
        var typeOwner = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var assembly in index.Assemblies.Where(a => a.Origin == AssemblyOrigin.Module && a.ModuleId is not null))
        {
            foreach (var type in assembly.DefinedTypeFullNames)
                typeOwner.TryAdd(type, assembly.ModuleId!.Value.Value);
        }

        var attribution = CrashAttributor.Attribute(
            record.Stack,
            record.PatchOwners,
            overlaps,
            type => typeOwner.TryGetValue(type, out var owner) ? owner : null);

        attribution = GameDataFeeds.Extend(attribution, entries, engineLog);

        var version = GameVersionReader.Read(install);

        return Compose(record, attribution, manifests, duplicates) with { GameVersion = version.IsEmpty ? string.Empty : version.ToString() };
    }

    private static ExceptionReport Compose(
        CrashGuardRecord record, StackAttribution attribution, IReadOnlyDictionary<string, ModuleManifest>? manifests,
        IReadOnlyList<(string Id, IReadOnlyList<string> Folders)> duplicates)
    {
        var strings = Strings.Current;

        // BEM's own module is on every guarded run and is never why the game failed.
        var involved = attribution.Involved.Where(m => !CompanionManifest.IsCompanionId(m.Module)).ToList();

        var frames = attribution.Frames.Select(f => FrameRow(strings, f)).ToList();

        var mods = (record.Modules ?? [])
            .Where(id => !CompanionManifest.IsCompanionId(id))
            .Select((id, at) => manifests is not null && manifests.TryGetValue(id, out var manifest)
                ? strings.Format("Core.DryRun.Exception.Mod.Row", at + 1, manifest.Name, manifest.VersionText ?? manifest.Version.ToString(), id)
                : strings.Format("Core.DryRun.Exception.Mod.RowBare", at + 1, id))
            .ToList();

        // Mods first, since the game's own assemblies are the same for everyone on that version.
        var assemblies = (record.Assemblies ?? [])
            .OrderBy(a => IsModAssembly(a) ? 0 : 1)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => strings.Format("Core.DryRun.Exception.Assembly.Row", a.Name, a.Version, a.Path))
            .ToList();

        var patches = (record.PatchOwners ?? [])
            .Select(p => strings.Format("Core.DryRun.Exception.Patch.Row", p.Type, p.Method, string.Join(", ", p.Modules)))
            .ToList();

        return new ExceptionReport(
            record,
            strings.Format("CrashView.Headline.Guard", record.ExceptionType, record.Where, record.Utc.ToLocalTime().ToString("g")),
            Notices(strings, record, duplicates, involved),
            [.. involved.Select(m => ModuleRow(strings, m))],
            [.. involved.Select(m => m.Module)],
            frames.Count == 0 ? [record.Stack] : frames,
            mods,
            assemblies,
            patches);
    }

    // Every module id more than one folder declares, with those folders in scan order.
    public static IReadOnlyList<(string Id, IReadOnlyList<string> Folders)> DuplicateIds(IEnumerable<ModuleManifest> modules) =>
        [.. modules
            .GroupBy(m => m.Id.Value, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => (g.Key, (IReadOnlyList<string>)[.. g.Select(m => m.FolderPath)]))];

    private static string Notices(
        ILocalizer strings, CrashGuardRecord record, IReadOnlyList<(string Id, IReadOnlyList<string> Folders)> duplicates,
        IReadOnlyList<InvolvedModule> involved)
    {
        var notices = new List<string>();

        foreach (var module in involved.Where(m => m.Engine is not null))
            notices.Add(EngineNotice(strings, module));

        foreach (var (id, folders) in duplicates)
            notices.Add(strings.Format("CrashView.Notice.DuplicateModuleId", id, string.Join(Environment.NewLine, folders)));

        if (!string.IsNullOrWhiteSpace(record.TickedParty))
            notices.Add(strings.Format("CrashView.Notice.TickedParty", record.TickedParty, PartyModule(record) ?? strings["CrashView.Notice.TickedParty.UnknownMod"]));

        if (CachedInitializerType(record.Stack) is { } type)
            notices.Add(strings.Format("CrashView.Notice.CachedInitializer", type));

        if (record.PatchOwners is not { Count: > 0 })
            notices.Add(strings["CrashView.Notice.NoOwners"]);

        return string.Join(Environment.NewLine + Environment.NewLine, notices);
    }

    // The mod folder holding the assembly the ticked party's component came from ("... from <assembly>").
    public static string? PartyModule(CrashGuardRecord record)
    {
        var text = record.TickedParty ?? string.Empty;
        var at = text.LastIndexOf(" from ", StringComparison.Ordinal);
        if (at < 0)
            return null;
        var assembly = text[(at + 6)..].Trim();
        var path = (record.Assemblies ?? []).FirstOrDefault(a => a.Name.Equals(assembly, StringComparison.OrdinalIgnoreCase))?.Path;
        if (string.IsNullOrEmpty(path))
            return null;
        var parts = path.Split('\\', '/');
        var modules = Array.FindLastIndex(parts, p => p.Equals("Modules", StringComparison.OrdinalIgnoreCase));
        return modules >= 0 && modules + 1 < parts.Length ? parts[modules + 1] : null;
    }

    // A type initializer that failed once fails the same way on every later use, so the stack it shows
    // is wherever the type was next touched, not where it first broke.
    public static string? CachedInitializerType(string? stack)
    {
        if (string.IsNullOrEmpty(stack))
            return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            stack, @"TypeInitializationException: The type initializer for '([^']+)' threw");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsModAssembly(CrashAssembly assembly) =>
        assembly.Path.Contains(@"\Modules\", StringComparison.OrdinalIgnoreCase)
        || assembly.Path.Contains(@"\workshop\content\", StringComparison.OrdinalIgnoreCase);

    // Plain words for a mod whose data file failed the game's own schema while the data the crash reads was loading.
    public static string EngineNotice(ILocalizer strings, InvolvedModule module)
    {
        var engine = module.Engine!;
        var others = Math.Max(0, engine.NullCount - engine.NamedIds.Count);
        var never = engine.NamedIds.Count == 0
            ? strings.Format("CrashView.Notice.EngineSchema.Unnamed", engine.NullCount)
            : others == 0
                ? string.Join(", ", engine.NamedIds)
                : strings.Format("CrashView.Notice.EngineSchema.Named", string.Join(", ", engine.NamedIds), others);

        return strings.Format("CrashView.Notice.EngineSchema", module.Module, engine.File, engine.ErrorCount, engine.Element, never);
    }

    public static string ModuleRow(ILocalizer strings, InvolvedModule module) =>
        module.Engine is { } engine
            ? strings.Format("CrashView.Module.EngineRow", module.Module, engine.File, engine.ErrorCount, engine.Element)
            : module.Best == FrameSource.DataFeed
            ? strings.Format("CrashView.Module.DataRow", module.Module, strings["CrashView.Source.Data"], string.Join(", ", module.Files))
            : ModuleRowFromFrames(strings, module);

    private static string ModuleRowFromFrames(ILocalizer strings, InvolvedModule module) =>
        strings.Format("CrashView.Module.Row", module.Module, strings.Plural("CrashView.Module.Frames", module.Frames), strings[module.Best switch
        {
            FrameSource.RecordedPatchOwner => "CrashView.Source.Recorded",
            FrameSource.StaticPatchScan => "CrashView.Source.Scan",
            _ => "CrashView.Source.Assembly",
        }]);

    public static string FrameRow(ILocalizer strings, AttributedFrame frame)
    {
        var name = $"{frame.Type}.{frame.Method}";

        if (frame.Modules.Count == 0)
            return name;

        var owners = string.Join(", ", frame.Modules);

        return strings.Format(frame.IsPatchFrame || frame.Source != FrameSource.ModAssembly ? "CrashView.Frame.Patched" : "CrashView.Frame.InMod", name, owners);
    }

    private IEnumerable<(string Title, IReadOnlyList<string> Rows)> Sections(string log)
    {
        var strings = Strings.Current;

        yield return (strings["Core.DryRun.Exception.Report.Suspects"], Suspects.Count == 0 ? [strings["CrashView.Modules.None"]] : Suspects);
        yield return (strings["Core.DryRun.Exception.Report.Stack"], Frames);
        yield return (strings["Core.DryRun.Exception.Report.RawStack"], [Record.Stack]);
        yield return (strings["Core.DryRun.Exception.Report.Mods"], Mods);
        yield return (strings["Core.DryRun.Exception.Report.Patches"], Patches);
        yield return (strings["Core.DryRun.Exception.Report.Assemblies"], Assemblies);

        if (!string.IsNullOrWhiteSpace(log))
            yield return (strings["Core.DryRun.Exception.Report.Log"], [log]);
    }

    // For the clipboard: what a mod author needs pasted into a bug report.
    public string ToText(string log = "")
    {
        var text = new StringBuilder();

        text.AppendLine(Strings.Current["Core.DryRun.Exception.Report.Title"]);
        text.AppendLine(Headline);

        if (GameVersion.Length > 0)
            text.AppendLine(Strings.Current.Format("Core.DryRun.Exception.Report.GameVersion", GameVersion));

        text.AppendLine(Record.Message);

        foreach (var (title, rows) in Sections(log))
        {
            if (rows.Count == 0)
                continue;

            text.AppendLine();
            text.AppendLine($"== {title} ==");

            foreach (var row in rows)
                text.AppendLine(row);
        }

        return text.ToString();
    }

    // One file with no script and nothing loaded from anywhere, so it opens the same on any machine it
    // is sent to. Every value is encoded: a message or a path is the mod's text, not markup.
    public string ToHtml(string log = "")
    {
        var html = new StringBuilder();
        var title = WebUtility.HtmlEncode(Strings.Current["Core.DryRun.Exception.Report.Title"]);

        html.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>").Append(title).Append("</title>")
            .Append("<style>body{background:#000000;color:#FFF1B3;font-family:Consolas,monospace;font-size:14px;margin:24px}")
            .Append("h1{font-size:20px;color:#FFDA00}h2{font-size:16px;color:#FFDA00;margin-top:24px}")
            .Append("pre{white-space:pre-wrap;word-break:break-word;margin:0}</style></head><body>")
            .Append("<h1>").Append(title).Append("</h1>")
            .Append("<pre>").Append(WebUtility.HtmlEncode(Headline)).Append('\n');

        if (GameVersion.Length > 0)
            html.Append(WebUtility.HtmlEncode(Strings.Current.Format("Core.DryRun.Exception.Report.GameVersion", GameVersion))).Append('\n');

        html.Append(WebUtility.HtmlEncode(Record.Message)).Append("</pre>");

        foreach (var (section, rows) in Sections(log))
        {
            if (rows.Count == 0)
                continue;

            html.Append("<h2>").Append(WebUtility.HtmlEncode(section)).Append("</h2><pre>")
                .Append(WebUtility.HtmlEncode(string.Join('\n', rows))).Append("</pre>");
        }

        return html.Append("</body></html>").ToString();
    }

    // One zip for sending to a mod author: the report both ways and, when there is one, the newest save
    // in the given folder. Returns that save's file name, or empty when none went in. The game may
    // still have the save open, so it is read shared.
    public string WriteBundle(string zipPath, string log, string savesFolder, IReadOnlyList<string>? attachments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);

        var save = NewestSave(savesFolder);
        var temp = zipPath + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Add(zip, "report.html", ToHtml(log));
            Add(zip, "report.txt", ToText(log));

            // The screenshot, when one was taken. A file that has gone since is left out, not an error.
            foreach (var attachment in (attachments ?? []).Where(File.Exists))
                _ = zip.CreateEntryFromFile(attachment, Path.GetFileName(attachment), CompressionLevel.Fastest);

            if (save is not null)
            {
                using var source = new FileStream(save, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var target = zip.CreateEntry(Path.GetFileName(save), CompressionLevel.Fastest).Open();

                source.CopyTo(target);
            }
        }

        File.Move(temp, zipPath, overwrite: true);

        return save is null ? string.Empty : Path.GetFileName(save);
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));

        writer.Write(text);
    }

    private static string? NewestSave(string savesFolder)
    {
        if (string.IsNullOrWhiteSpace(savesFolder) || !Directory.Exists(savesFolder))
            return null;

        return new DirectoryInfo(savesFolder).EnumerateFiles("*.sav", SearchOption.AllDirectories)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    // The game's process id, which the Companion puts in the run id: "<armed id>-<pid>" for a guard run and
    // "<armed id>-<pid>-<HHmmssfff>" for a watched launch, whose last segment is the clock, never the pid.
    // Zero when it is not there.
    public static int ProcessIdOf(string? runId)
    {
        if (string.IsNullOrEmpty(runId))
            return 0;

        var parts = runId.Split('-');
        var last = parts[^1];

        if (parts.Length >= 3 && last.Length == 9 && last.All(char.IsAsciiDigit))
            last = parts[^2];

        return int.TryParse(last, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var processId) && processId > 0 ? processId : 0;
    }

    // The engine's own log for the run that crashed, last lines only. The game still has the file open.
    public static string ReadLogTail(string logsFolder, string runId, int maxLines = 200)
    {
        try
        {
            var processId = ProcessIdOf(runId);

            if (processId == 0 || !Directory.Exists(logsFolder))
                return string.Empty;

            var path = Path.Combine(logsFolder, $"rgl_log_{processId}.txt");

            if (!File.Exists(path))
                return string.Empty;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var tail = new Queue<string>(maxLines);

            while (reader.ReadLine() is { } line)
            {
                if (tail.Count == maxLines)
                    tail.Dequeue();

                tail.Enqueue(line);
            }

            return string.Join(Environment.NewLine, tail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
