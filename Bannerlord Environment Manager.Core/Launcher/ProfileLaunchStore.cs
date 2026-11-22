using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Launcher;

// A named set of launch arguments, such as "Windowed debug" or "Cheats on".
public sealed record LaunchArgumentPreset(string Name, string Arguments);

// A program the owner runs beside or instead of the game for one profile, as Mod Organizer 2's
// executables list holds them: a modding tool, a log viewer, a patched launcher. The path is whatever
// the owner typed; Check says whether it can be started right now.
public sealed record CustomExecutable(string Name, string Path, string Arguments = "", string? WorkingDirectory = null)
{
    public const int MaxNameLength = 60;

    public const int MaxArgumentsLength = 2000;

    // Null when it can run; otherwise a short reason. A relative path is refused rather than resolved
    // against whatever the working directory happens to be, since that varies with how BEM was started.
    public string? Check()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "It has no name.";

        if (string.IsNullOrWhiteSpace(Path) || !System.IO.Path.IsPathFullyQualified(Path))
            return "The path is not a full path.";

        if (!File.Exists(Path))
            return "The file does not exist.";

        if (!string.IsNullOrWhiteSpace(WorkingDirectory)
            && (!System.IO.Path.IsPathFullyQualified(WorkingDirectory) || !Directory.Exists(WorkingDirectory)))
            return "The working directory does not exist.";

        return null;
    }

    public string EffectiveWorkingDirectory =>
        !string.IsNullOrWhiteSpace(WorkingDirectory) ? WorkingDirectory : System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

// What one profile launches with: its argument presets, which one is in force, and its executables.
public sealed record ProfileLaunch(
    IReadOnlyList<LaunchArgumentPreset> Presets,
    string? ActivePreset,
    IReadOnlyList<CustomExecutable> Executables)
{
    public static ProfileLaunch Empty { get; } = new([], null, []);

    public bool IsEmpty => Presets.Count == 0 && Executables.Count == 0 && ActivePreset is null;

    // The arguments the active preset adds, or nothing when none is chosen or the chosen one is gone.
    public string ActiveArguments =>
        Presets.FirstOrDefault(p => string.Equals(p.Name, ActivePreset, StringComparison.OrdinalIgnoreCase))?.Arguments ?? string.Empty;

    // Preset arguments go after the arguments typed on the Play page, separated by one space, so the
    // page's own text keeps leading and an empty side adds no stray space.
    public string Combine(string? pageArguments) =>
        string.Join(' ', new[] { pageArguments?.Trim(), ActiveArguments.Trim() }.Where(text => !string.IsNullOrEmpty(text)));
}

// Per game version, like the profiles it hangs off: a profile's name is only meaningful beside the
// profiles of that version.
public sealed class ProfileLaunchStore(string? filePath)
{
    public const string FileName = "profile-launch.json";

    public const int MaxPresets = 20;

    public const int MaxExecutables = 30;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string? FilePath { get; } = filePath;

    public static string DefaultPath(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, FileName);

    public ProfileLaunch Get(string profileName) =>
        ReadAll().TryGetValue(profileName?.Trim() ?? string.Empty, out var launch) ? Clean(launch) : ProfileLaunch.Empty;

    // An empty ProfileLaunch removes the entry, so a profile with nothing set leaves nothing behind.
    public bool Set(string profileName, ProfileLaunch launch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentNullException.ThrowIfNull(launch);

        if (string.IsNullOrWhiteSpace(FilePath))
            return false;

        var all = ReadAll();
        var clean = Clean(launch);

        if (clean.IsEmpty)
            all.Remove(profileName.Trim());
        else
            all[profileName.Trim()] = clean;

        return Write(all);
    }

    // A renamed profile keeps what it launches with; a deleted one drops it.
    public bool Rename(string from, string to)
    {
        var all = ReadAll();

        if (!all.Remove(from?.Trim() ?? string.Empty, out var launch))
            return false;

        all[to.Trim()] = launch;
        return Write(all);
    }

    public bool Forget(string profileName)
    {
        var all = ReadAll();

        return all.Remove(profileName?.Trim() ?? string.Empty) && Write(all);
    }

    private static ProfileLaunch Clean(ProfileLaunch launch)
    {
        var presets = launch.Presets
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new LaunchArgumentPreset(Cap(p.Name.Trim(), CustomExecutable.MaxNameLength), Cap((p.Arguments ?? string.Empty).Trim(), CustomExecutable.MaxArgumentsLength)))
            .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxPresets)
            .ToList();

        var executables = launch.Executables
            .Where(e => !string.IsNullOrWhiteSpace(e.Name) && !string.IsNullOrWhiteSpace(e.Path))
            .Select(e => new CustomExecutable(
                Cap(e.Name.Trim(), CustomExecutable.MaxNameLength),
                e.Path.Trim(),
                Cap((e.Arguments ?? string.Empty).Trim(), CustomExecutable.MaxArgumentsLength),
                string.IsNullOrWhiteSpace(e.WorkingDirectory) ? null : e.WorkingDirectory.Trim()))
            .DistinctBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxExecutables)
            .ToList();

        var active = presets.FirstOrDefault(p => string.Equals(p.Name, launch.ActivePreset?.Trim(), StringComparison.OrdinalIgnoreCase))?.Name;

        return new ProfileLaunch(presets, active, executables);
    }

    private static string Cap(string text, int length) => text.Length > length ? text[..length].TrimEnd() : text;

    private Dictionary<string, ProfileLaunch> ReadAll()
    {
        var all = new Dictionary<string, ProfileLaunch>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
            return all;

        try
        {
            foreach (var (name, launch) in JsonSerializer.Deserialize<Dictionary<string, ProfileLaunch>>(File.ReadAllText(FilePath)) ?? [])
            {
                if (launch is not null && !string.IsNullOrWhiteSpace(name))
                    all[name] = launch with { Presets = launch.Presets ?? [], Executables = launch.Executables ?? [] };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, ProfileLaunch>(StringComparer.OrdinalIgnoreCase);
        }

        return all;
    }

    private bool Write(Dictionary<string, ProfileLaunch> all)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath!)!);

            var temporary = FilePath + ".tmp";

            File.WriteAllText(temporary, JsonSerializer.Serialize(all, Format));
            File.Move(temporary, FilePath!, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
