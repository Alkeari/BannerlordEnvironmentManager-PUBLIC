using System.Text.Json;

namespace BannerlordEnvironmentManager.Core.Instances;

// Beside the app's own settings.json rather than inside it: that file is a private record in the
// Library view model, and Core cannot see it. This one is the instance system's, and holds nothing
// a user would miss if it were deleted.
public sealed class InstanceSettingsStore(string? filePath = null)
{
    public const string FileName = "instances.json";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(AppDataRoot.Folder, FileName);

    public string FilePath { get; } = filePath ?? DefaultPath;

    public InstanceSettings Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<InstanceSettings>(File.ReadAllText(FilePath), Format)
                  ?? InstanceSettings.Empty
                : InstanceSettings.Empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return InstanceSettings.Empty;
        }
    }

    // True only for a data root nothing has configured yet: no file, or a file that reads back with
    // every field empty. A file that cannot be read right now (another BEM is rewriting it) is not
    // fresh, because Read() answers Empty for it and adopting on that answer wrote one machine's
    // resting id into a data root that knew nothing of it.
    public bool IsFresh()
    {
        try
        {
            if (!File.Exists(FilePath))
                return true;

            var read = JsonSerializer.Deserialize<InstanceSettings>(File.ReadAllText(FilePath), Format);

            return read is null
                || (string.IsNullOrEmpty(read.GamesRoot) && string.IsNullOrEmpty(read.RestingInstanceId)
                    && string.IsNullOrEmpty(read.ActiveInstanceId) && string.IsNullOrEmpty(read.SteamAccountName));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Write(InstanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Format));
        File.Move(temporary, FilePath, overwrite: true);
    }

    // Proposes the drive with the most free space, since a version is roughly 95 GB.
    public static string ProposeGamesRoot()
    {
        var drive = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .OrderByDescending(d => d.AvailableFreeSpace)
            .FirstOrDefault();

        return Path.Combine(drive?.RootDirectory.FullName ?? @"C:\", "Bannerlord Versions");
    }
}
