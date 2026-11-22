using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.DryRun;

// What the Companion wrote when it caught a crash inside the game. The field names are a contract with
// CrashGuard in the Companion.
// The mods whose Harmony patches sat on one method of the stack when the exception was caught.
public sealed record CrashPatchOwners(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("modules")] IReadOnlyList<string> Modules);

public sealed record CrashAssembly(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("path")] string Path);

public sealed record CrashGuardRecord(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("utc")] DateTime Utc,
    [property: JsonPropertyName("exceptionType")] string ExceptionType,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("stack")] string Stack,
    [property: JsonPropertyName("where")] string Where,
    [property: JsonPropertyName("saveName")] string SaveName,
    [property: JsonPropertyName("saveResult")] string SaveResult,
    [property: JsonPropertyName("patchOwners")] IReadOnlyList<CrashPatchOwners>? PatchOwners = null,
    // The game build the run was on, and each enabled module's file stamp (LaunchEvidence.StampOf), so a
    // later finding can tell whether this crash was this same mod file on this same game version. Absent
    // on records written before these were kept, which therefore prove nothing.
    [property: JsonPropertyName("gameVersion")] string? GameVersion = null,
    [property: JsonPropertyName("moduleStamps")] IReadOnlyDictionary<string, string>? ModuleStamps = null)
{
    public static IReadOnlyList<CrashGuardRecord> ReadAll(string folder)
    {
        var all = new List<CrashGuardRecord>();

        if (!Directory.Exists(folder))
            return all;

        foreach (var file in new DirectoryInfo(folder).GetFiles("*.json").OrderBy(f => f.LastWriteTimeUtc))
        {
            try
            {
                if (JsonSerializer.Deserialize<CrashGuardRecord>(File.ReadAllText(file.FullName)) is { } record)
                    all.Add(record);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        return all;
    }

    // A record the Companion wrote without stamps is given them from the files as they stand, but only
    // when nothing the stamp covers was written after the crash: a mod updated since then is a different
    // file, and its crash is not evidence about the new one. The game build is held to the same test.
    public CrashGuardRecord WithStampsFromDisk() => WithStampsFromDisk(install => BannerlordEnvironmentManager.Core.Modules.ModuleScanner.ScanAll(install));

    // Stamps every record, scanning each install once however many records name it. A scan checks the
    // official modules' Authenticode signatures, so 22 records meant 22 scans, 80 ms to 500 ms each,
    // back to back on the UI thread: a 20 s freeze.
    public static IReadOnlyList<CrashGuardRecord> StampAll(
        IEnumerable<CrashGuardRecord> records,
        Func<string, BannerlordEnvironmentManager.Core.Modules.ModuleScanResult>? scan = null)
    {
        var scanner = scan ?? (install => BannerlordEnvironmentManager.Core.Modules.ModuleScanner.ScanAll(install));
        var scans = new Dictionary<string, BannerlordEnvironmentManager.Core.Modules.ModuleScanResult>(StringComparer.OrdinalIgnoreCase);

        BannerlordEnvironmentManager.Core.Modules.ModuleScanResult Shared(string install)
        {
            if (!scans.TryGetValue(install, out var result))
                scans[install] = result = scanner(install);

            return result;
        }

        return [.. records.Select(record => record.WithStampsFromDisk(Shared))];
    }

    public CrashGuardRecord WithStampsFromDisk(Func<string, BannerlordEnvironmentManager.Core.Modules.ModuleScanResult> scanner)
    {
        if (ModuleStamps is { Count: > 0 } && !string.IsNullOrEmpty(GameVersion))
            return this;

        var install = GamePath ?? string.Empty;
        if (install.Length == 0 || !Directory.Exists(install))
            return this;

        var crashed = Utc.ToUniversalTime();
        var platform = (Assemblies ?? []).Any(a => a.Path.Contains(@"\Gaming.Desktop.x64_Shipping_Client\", StringComparison.OrdinalIgnoreCase))
            ? "Gaming.Desktop.x64_Shipping_Client"
            : BannerlordEnvironmentManager.Core.Game.GameInstallLocator.StandardBinaryFolder;

        string? version = GameVersion;
        if (string.IsNullOrEmpty(version))
        {
            var native = Path.Combine(install, "Modules", "Native", "SubModule.xml");
            if (File.Exists(native) && File.GetLastWriteTimeUtc(native) <= crashed)
            {
                var read = BannerlordEnvironmentManager.Core.Game.GameVersionReader.Read(install);
                version = read.IsEmpty ? null : read.ToString();
            }
        }

        var stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scan = scanner(install);
        if (!scan.Failed)
        {
            var folders = scan.Modules
                .GroupBy(m => m.Id.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().FolderPath, StringComparer.OrdinalIgnoreCase);

            foreach (var id in this.Modules ?? [])
            {
                if (!folders.TryGetValue(id, out var folder))
                    continue;

                var bin = new DirectoryInfo(Path.Combine(folder, "bin", platform));
                if (!bin.Exists || bin.EnumerateFiles("*.dll").Any(f => f.LastWriteTimeUtc > crashed))
                    continue;

                var stamp = BannerlordEnvironmentManager.Core.Launcher.LaunchEvidence.StampOf(folder, platform);
                if (stamp.Length > 0)
                    stamps[id] = stamp;
            }
        }

        return this with { GameVersion = version, ModuleStamps = stamps.Count > 0 ? stamps : ModuleStamps };
    }

    public static string DefaultFolder => Path.Combine(AppDataRoot.Folder, "crash-guard");

    // Everything below was added with the Bannerlord Exception Manager's window. A record written by an
    // older Companion carries none of it and reads as a crash nothing can be done about.

    // 1 for the first crash of a run, counting up for each one the player continued past.
    [JsonPropertyName("crashNumber")]
    public int CrashNumber { get; init; }

    // False when the process was already ending as the record was written.
    [JsonPropertyName("canContinue")]
    public bool CanContinue { get; init; }

    [JsonPropertyName("canSave")]
    public bool CanSave { get; init; }

    [JsonPropertyName("inMission")]
    public bool InMission { get; init; }

    // The party a periodic campaign tick was working on when it threw, as the Companion described it.
    [JsonPropertyName("tickedParty")]
    public string? TickedParty { get; init; }

    [JsonPropertyName("gamePath")]
    public string? GamePath { get; init; }

    // The load order the run was started with, in order.
    [JsonPropertyName("modules")]
    public IReadOnlyList<string>? Modules { get; init; }

    [JsonPropertyName("assemblies")]
    public IReadOnlyList<CrashAssembly>? Assemblies { get; init; }

    public static CrashGuardRecord? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<CrashGuardRecord>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Saved => string.Equals(SaveResult, "Success", StringComparison.OrdinalIgnoreCase);

    // A half-written or unreadable record is skipped, so an older good one still shows.
    public static CrashGuardRecord? ReadNewest(string folder)
    {
        if (!Directory.Exists(folder))
            return null;

        foreach (var file in new DirectoryInfo(folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            try
            {
                if (JsonSerializer.Deserialize<CrashGuardRecord>(File.ReadAllText(file.FullName)) is { } record)
                    return record;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }
}

// The owner's switch. The guard is on unless he turned it off. Earlier versions armed it by writing this
// file, so a bare file (no "enabled" value) still means on; only an explicit false turns it off, and a
// file that cannot be read leaves it on.
public sealed class CrashGuardSwitch(string stateFilePath)
{
    public static string DefaultStateFilePath => Path.Combine(AppDataRoot.Folder, "crash-guard.json");

    public bool IsOn
    {
        get
        {
            try
            {
                if (!File.Exists(StateFilePath))
                    return true;

                using var document = JsonDocument.Parse(File.ReadAllText(StateFilePath));

                return !(document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("enabled", out var enabled)
                    && enabled.ValueKind == JsonValueKind.False);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return true;
            }
        }
    }

    public string StateFilePath { get; } = stateFilePath;

    public void Set(bool on)
    {
        if (on)
        {
            if (File.Exists(StateFilePath))
                File.Delete(StateFilePath);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
            File.WriteAllText(StateFilePath, "{\"enabled\":false}");
        }
    }
}
