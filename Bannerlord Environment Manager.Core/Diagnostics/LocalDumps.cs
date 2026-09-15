using BannerlordEnvironmentManager.Core.Localization;
using Microsoft.Win32;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// Windows Error Reporting's own DumpType values. Nothing else is valid there, so nothing else is
// modeled here: a number outside this set in the registry is reported as the number it is.
public enum LocalDumpType
{
    Custom = 0,
    Mini = 1,
    Full = 2
}

// What one executable's LocalDumps subkey holds, or what Windows will do in its absence.
public sealed record LocalDumpKey(bool Exists, int? DumpType = null, int? DumpCount = null, string? DumpFolder = null);

// The effective settings for one executable, plus the key exactly as it was found. Restoring reads
// the second: a key that inherited its DumpType from somewhere else has to go back to inheriting it,
// not to a value BEM wrote down for it.
public sealed record LocalDumpSetting(
    string Executable,
    string RegistryPath,
    LocalDumpKey Found,
    int DumpType,
    int DumpCount,
    string DumpFolder)
{
    public bool HasItsOwnKey => Found.Exists;

    public bool IsFull => DumpType == (int)LocalDumpType.Full;

    public string DescribeType() => DumpType switch
    {
        (int)LocalDumpType.Custom => Strings.Current["Core.Diagnostics.LocalDumps.Type.Custom"],
        (int)LocalDumpType.Mini => Strings.Current["Core.Diagnostics.LocalDumps.Type.Mini"],
        (int)LocalDumpType.Full => Strings.Current["Core.Diagnostics.LocalDumps.Type.Full"],
        _ => Strings.Current.Format("Core.Diagnostics.LocalDumps.Type.Unrecognized", DumpType)
    };
}

public sealed record LocalDumpReport(
    bool CollectionEnabled,
    IReadOnlyList<LocalDumpSetting> Settings,
    string? Error = null)
{
    public bool AllFull => Settings.Count > 0 && Settings.All(s => s.IsFull);

    public bool AnyFull => Settings.Any(s => s.IsFull);

    // The honest cost of what is configured, said where the user would otherwise assume a dump
    // answers the question. A mini dump omits most process memory, and JIT-compiled code lives in
    // exactly the memory it omits, so a frame belonging to a mod's own compiled method has nothing
    // behind it to resolve. That is why a mini dump gives the exception and a hole in the stack.
    public string Describe()
    {
        if (Error is not null)
            return Strings.Current.Format("Core.Diagnostics.LocalDumps.Describe.Error", Error);

        if (!CollectionEnabled)
            return Strings.Current["Core.Diagnostics.LocalDumps.Describe.NotCollected"];

        if (AllFull)
            return Strings.Current["Core.Diagnostics.LocalDumps.Describe.AllFull"];

        var mini = Settings.Where(s => !s.IsFull).ToList();

        // Two sentences for the same reading, because only the outer ring can offer to change the
        // setting. There, the shortfall is an argument for the button beside it and ends on what a
        // full dump would buy. Here, it is the reason a frame in the dump list has no body behind it,
        // and it stops there rather than ending on a remedy nothing on screen carries.
        return Strings.Current.Format(
#if DEV_BEM
            "Core.Diagnostics.LocalDumps.Describe.Mini",
#else
            "Core.Diagnostics.LocalDumps.Describe.MiniAsFound",
#endif
            mini[0].DescribeType(), string.Join(", ", mini.Select(s => s.Executable)));
    }
}

// One registry change, as the text that makes it, so nothing is written that the user has not read
// first. It is a .reg script because that is the form of registry edit a person can inspect, keep,
// and hand to somebody else, and because one elevation covers every key in it.
public sealed record LocalDumpPlan(
    string Title,
    string Script,
    IReadOnlyList<string> RegistryPaths,
    string Note);

public static class LocalDumps
{
    public const string RootPath = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps";

    private const string RootKey = @"HKEY_LOCAL_MACHINE\" + RootPath;

    // Windows' own defaults when LocalDumps carries no value: the local application data folder,
    // ten dumps kept, and a mini dump.
    private const int DefaultDumpType = (int)LocalDumpType.Mini;

    private const int DefaultDumpCount = 10;

    // The processes the game itself faults in. Deliberately not CrashArtifactPaths' watched list:
    // that one also carries the crash uploader, which BEM watches only so it can copy files out
    // before the uploader deletes them, and whose own crash is nobody's mod.
    public static IReadOnlyList<string> GameExecutables { get; } =
    [
        "Bannerlord.exe",
        "Bannerlord.Native.exe",
        "Bannerlord_BE.exe",
        "Bannerlord.BLSE.Standalone.exe",
        "Bannerlord.BLSE.Launcher.exe",
        "Bannerlord.BLSE.LauncherEx.exe",
        "TaleWorlds.MountAndBlade.Launcher.exe",
        "Launcher.Native.exe"
    ];

    // Where the restore script is kept, so turning this off later does not depend on BEM remembering
    // anything in memory or on the user having the window still open.
    public static string GetRestoreScriptPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager Diagnostics",
        "local-dumps-restore.reg");

    public static LocalDumpReport Read() => Read(ReadFromRegistry, GameExecutables);

    public static LocalDumpReport Read(Func<string, LocalDumpKey> readKey, IEnumerable<string> executables)
    {
        ArgumentNullException.ThrowIfNull(readKey);
        ArgumentNullException.ThrowIfNull(executables);

        LocalDumpKey root;

        try
        {
            root = readKey(RootPath);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                       or UnauthorizedAccessException
                                       or IOException)
        {
            return new LocalDumpReport(false, [], ex.Message);
        }

        var settings = new List<LocalDumpSetting>();

        foreach (var executable in executables)
        {
            var path = RootPath + "\\" + executable;
            LocalDumpKey key;

            try
            {
                key = readKey(path);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException
                                           or UnauthorizedAccessException
                                           or IOException)
            {
                return new LocalDumpReport(root.Exists, settings, ex.Message);
            }

            settings.Add(new LocalDumpSetting(
                executable,
                @"HKEY_LOCAL_MACHINE\" + path,
                key,
                key.DumpType ?? root.DumpType ?? DefaultDumpType,
                key.DumpCount ?? root.DumpCount ?? DefaultDumpCount,
                key.DumpFolder ?? root.DumpFolder ?? CrashDumps.GetDefaultFolder()));
        }

        return new LocalDumpReport(root.Exists, settings);
    }

    // Turning it on. Every path and value is in the script, the script is what the user is shown, and
    // nothing about this runs unless they say so: a registry edit is a change outside BEM's own state
    // and needs administrator rights, which is exactly the category that is never a default.
    public static LocalDumpPlan PlanFullDumps(LocalDumpReport report, int dumpCount)
    {
        ArgumentNullException.ThrowIfNull(report);

        var wanted = report.Settings.Where(s => !s.IsFull || s.DumpCount != dumpCount).ToList();
        var lines = new List<string> { "Windows Registry Editor Version 5.00", string.Empty };

        // The root key has to exist for Windows to keep a dump for anything at all. Naming it with no
        // values under it is what turns collection on without changing anybody else's settings.
        if (!report.CollectionEnabled)
        {
            lines.Add($"[{RootKey}]");
            lines.Add(string.Empty);
        }

        foreach (var setting in wanted)
        {
            lines.Add($"[{setting.RegistryPath}]");
            lines.Add($"\"DumpType\"=dword:{(int)LocalDumpType.Full:x8}");
            lines.Add($"\"DumpCount\"=dword:{dumpCount:x8}");
            lines.Add(string.Empty);
        }

        return new LocalDumpPlan(
            Strings.Current["Core.Diagnostics.LocalDumps.Plan.FullDumps.Title"],
            string.Join(Environment.NewLine, lines),
            [.. wanted.Select(s => s.RegistryPath)],
            Strings.Current.Format(
                "Core.Diagnostics.LocalDumps.Plan.FullDumps.Note",
                dumpCount,
                report.Settings.Count > 0 ? report.Settings[0].DumpFolder : CrashDumps.GetDefaultFolder()));
    }

    // Turning it off again, which is the same decision and has to be as reachable. It restores what
    // was there rather than deleting keys: a key BEM did not create is put back with the values it
    // had, and only a key BEM created is removed.
    public static LocalDumpPlan PlanRestore(LocalDumpReport before)
    {
        ArgumentNullException.ThrowIfNull(before);

        var lines = new List<string> { "Windows Registry Editor Version 5.00", string.Empty };
        var paths = new List<string>();

        foreach (var setting in before.Settings)
        {
            paths.Add(setting.RegistryPath);

            if (!setting.HasItsOwnKey)
            {
                // A key BEM added and nothing else uses. The leading minus is how a .reg script
                // removes a key, and it removes only this one.
                lines.Add($"[-{setting.RegistryPath}]");
                lines.Add(string.Empty);
                continue;
            }

            // A value the key did not hold goes back to not being held. The lone minus is how a .reg
            // script deletes one value and leaves the rest of the key alone.
            lines.Add($"[{setting.RegistryPath}]");
            lines.Add(setting.Found.DumpType is { } type
                ? $"\"DumpType\"=dword:{type:x8}"
                : "\"DumpType\"=-");
            lines.Add(setting.Found.DumpCount is { } count
                ? $"\"DumpCount\"=dword:{count:x8}"
                : "\"DumpCount\"=-");
            lines.Add(string.Empty);
        }

        return new LocalDumpPlan(
            Strings.Current["Core.Diagnostics.LocalDumps.Plan.Restore.Title"],
            string.Join(Environment.NewLine, lines),
            paths,
            Strings.Current["Core.Diagnostics.LocalDumps.Plan.Restore.Note"]);
    }

    private static LocalDumpKey ReadFromRegistry(string path)
    {
        if (!OperatingSystem.IsWindows())
            return new LocalDumpKey(false);

        using var key = Registry.LocalMachine.OpenSubKey(path);

        return key is null
            ? new LocalDumpKey(false)
            : new LocalDumpKey(
                true,
                key.GetValue("DumpType") as int?,
                key.GetValue("DumpCount") as int?,
                key.GetValue("DumpFolder") as string);
    }
}
