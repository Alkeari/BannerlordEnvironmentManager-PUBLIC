using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules;

public sealed record UnreadableModule(string FolderName, string FolderPath, string Error, string? DeclaredId = null);

// A folder sitting directly under Modules with no SubModule.xml in it. The game loads nothing from one,
// but BUTR's ModuleInfoHelper.GetPhysicalModules enumerates every directory under Modules and reads
// <folder>\SubModule.xml without testing for it first, so each such folder throws a caught
// FileNotFoundException once per library that asks. On a real install that is 20 throws a launch
// from ButterLib, UIExtenderEx and MCM between them, every one of them carrying a full stack.
//
// FileNames is capped, so it says what the folder holds without promising to list all of it; FileCount
// and SizeBytes are the true totals.
public sealed record ModuleFolderWithoutManifest(
    string FolderName,
    string FolderPath,
    int FileCount,
    long SizeBytes,
    IReadOnlyList<string> FileNames);

public sealed record ModuleScanResult(
    IReadOnlyList<ModuleManifest> Modules,
    IReadOnlyList<UnreadableModule> Unreadable,
    string? Error = null,
    IReadOnlyList<DuplicateModule> Shadowed = null!,
    IReadOnlyList<ModuleFolderWithoutManifest> FoldersWithoutManifest = null!,
    string? WorkshopError = null,
    IReadOnlyList<DuplicateModuleIdGroup> DuplicateIds = null!)
{
    // A failing Workshop scan is non-fatal by design: most users have no subscriptions and the
    // game reaches these folders only through the Steam API, never a folder scan. But it must not
    // be silent either, or a caller cannot tell "the Workshop folder was skipped" from "it is
    // genuinely empty". WorkshopError carries that distinction; Failed stays the Modules/ answer.
    public bool WorkshopFailed => WorkshopError is not null;
    // A Workshop copy that ScanAll dropped is gone from Modules by the time anything downstream looks,
    // so the folder it was dropped from has to be recorded here or the user can never be told which
    // subscription is sitting inert on disk.
    public IReadOnlyList<DuplicateModule> Shadowed { get; init; } = Shadowed ?? [];

    public IReadOnlyList<ModuleFolderWithoutManifest> FoldersWithoutManifest { get; init; } =
        FoldersWithoutManifest ?? [];

    // Two folders under the scanned root declaring one module id. Shadowed is the other question and
    // stays separate: a Workshop copy that lost to Modules is inert and the game still starts, while
    // this pair stops it loading and the launcher's error names neither folder.
    public IReadOnlyList<DuplicateModuleIdGroup> DuplicateIds { get; init; } = DuplicateIds ?? [];

    public static ModuleScanResult Empty { get; } = new([], []);

    public bool Failed => Error is not null;
}

public static class ModuleScanner
{
    // The Workshop content folder is keyed by app id, not by game name; Bannerlord's is the same
    // app the game and its downloader use everywhere else.
    private static readonly string WorkshopAppId = GameDlc.BaseAppId.ToString();

    public static string GetModulesFolder(string gameInstallPath) =>
        Path.Combine(gameInstallPath, "Modules");

    // The install path can sit anywhere under steamapps\common\<game>, so the workshop
    // sibling is found by walking up to the steamapps folder itself rather than assuming
    // a fixed depth.
    public static string? GetWorkshopFolder(string gameInstallPath)
    {
        if (string.IsNullOrWhiteSpace(gameInstallPath))
            return null;

        var directory = new DirectoryInfo(gameInstallPath);

        while (directory is not null && !string.Equals(directory.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
            directory = directory.Parent;

        return directory is null
            ? null
            : Path.Combine(directory.FullName, "workshop", "content", WorkshopAppId);
    }

    // A Workshop scan failure is never fatal: most users have no subscriptions at all, and the
    // game itself only reaches these folders through the Steam API, never a folder scan. A
    // failure reading Modules/ is the one that matters and is returned untouched.
    //
    // The audit runs here rather than in Scan because a Workshop module's voucher can live in Modules
    // and the other way round; auditing each folder alone would demote a module the merged picture
    // corroborates. Core tests pass the check explicitly so nothing depends on registration order.
    public static ModuleScanResult ScanAll(string gameInstallPath, OfficialSignatureCheck? signatureCheck = null)
    {
        var cursor = System.Diagnostics.Stopwatch.StartNew();
        var result = ScanAllCore(gameInstallPath, signatureCheck);
        cursor.Stop();
        if (cursor.ElapsedMilliseconds >= 20)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bem_perf.log"),
                    $"[P] {DateTime.Now:HH:mm:ss.fff} ScanAll elapsed={cursor.ElapsedMilliseconds}ms modules={result.Modules.Count} thread={(Environment.CurrentManagedThreadId != 1 ? "W" : "UI")} caller={new System.Diagnostics.StackTrace(1, false)?.GetFrame(0)?.GetMethod()?.DeclaringType?.Name}\r\n");
            }
            catch
            {
            }
        }
        return result;
    }

    private static ModuleScanResult ScanAllCore(string gameInstallPath, OfficialSignatureCheck? signatureCheck = null)
    {
        var local = Scan(GetModulesFolder(gameInstallPath));

        if (local.Failed)
            return local;

        var check = signatureCheck ?? OfficialClaimVerifier.Registered;
        var workshopFolder = GetWorkshopFolder(gameInstallPath);

        if (workshopFolder is null || !Directory.Exists(workshopFolder))
            return local with { Modules = OfficialClaimAudit.Apply(local.Modules, check) };

        var workshop = Scan(workshopFolder);
        var seen = local.Modules.Select(m => m.Id).ToHashSet();

        var merged = new List<ModuleManifest>(local.Modules);
        var shadowed = new Dictionary<ModuleId, List<string>>();

        foreach (var manifest in workshop.Modules)
        {
            if (seen.Add(manifest.Id))
                merged.Add(manifest with { Source = ModuleSource.Workshop });
            else if (shadowed.TryGetValue(manifest.Id, out var folders))
                folders.Add(manifest.FolderPath);
            else
                shadowed[manifest.Id] = [manifest.FolderPath];
        }

        return new ModuleScanResult(
            OfficialClaimAudit.Apply(merged, check),
            [.. local.Unreadable, .. workshop.Unreadable],
            null,
            [.. shadowed.Select(entry => new DuplicateModule(
                entry.Key,
                merged.First(m => m.Id == entry.Key).FolderPath,
                entry.Value))],
            [.. local.FoldersWithoutManifest, .. workshop.FoldersWithoutManifest],
            WorkshopError: workshop.Error,
            // The local answer only. Two Workshop folders declaring one id already come out as
            // Shadowed above, since the second loses the seen-id guard, so carrying the Workshop
            // scan's own duplicates here would report the same pair twice.
            DuplicateIds: local.DuplicateIds);
    }

    public static ModuleScanResult Scan(string modulesFolderPath)
    {
        if (string.IsNullOrWhiteSpace(modulesFolderPath))
            return ModuleScanResult.Empty;

        string[] folders;

        try
        {
            folders = Directory.GetDirectories(modulesFolderPath);
        }
        catch (DirectoryNotFoundException)
        {
            return ModuleScanResult.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ModuleScanResult([], [], Strings.Current.Format("Core.Modules.Scanner.FolderUnreadable", modulesFolderPath, ex.Message));
        }

        var modules = new List<ModuleManifest>();
        var unreadable = new List<UnreadableModule>();
        var withoutManifest = new List<ModuleFolderWithoutManifest>();

        foreach (var folder in folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(folder, "SubModule.xml");

            // Skipping this folder silently is what let a folder the game cannot load, and that every
            // BUTR library throws over, sit on a real install unreported. It is described instead,
            // with enough of its contents for a person to tell leftovers from a mod they want back.
            if (!File.Exists(manifestPath))
            {
                withoutManifest.Add(Describe(folder));
                continue;
            }

            // A downloader that preallocates its destination file leaves SubModule.xml at zero length
            // or full of NUL bytes for a moment before the archive fills it in; the Modules watcher can
            // now rescan mid-download, where it never looked before, so that shape is skipped rather
            // than reported. Nothing is added for the folder this pass: it is not a module yet, the
            // same as a folder whose SubModule.xml has not been written at all, and the next scan picks
            // it up once the download has moved on.
            if (SubModuleXmlParser.LooksLikePartialWrite(manifestPath))
                continue;

            if (SubModuleXmlParser.TryLoad(manifestPath, out var manifest, out var error))
                modules.Add(manifest);
            else
                unreadable.Add(new UnreadableModule(
                    Path.GetFileName(folder),
                    folder,
                    error ?? "Unknown error.",
                    SubModuleXmlParser.TryRecoverDeclaredId(manifestPath)));
        }

        return new ModuleScanResult(
            modules,
            unreadable,
            FoldersWithoutManifest: withoutManifest,
            DuplicateIds: DuplicateModuleIds.Find(modules));
    }

    private const int NamedFileLimit = 10;

    private static ModuleFolderWithoutManifest Describe(string folder)
    {
        var names = new List<string>();
        long sizeBytes = 0;
        var fileCount = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }))
            {
                fileCount++;

                if (names.Count < NamedFileLimit)
                    names.Add(Path.GetRelativePath(folder, file));

                try
                {
                    sizeBytes += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new ModuleFolderWithoutManifest(Path.GetFileName(folder), folder, fileCount, sizeBytes, names);
    }
}
