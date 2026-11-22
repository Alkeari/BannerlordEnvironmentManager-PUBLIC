using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Modules;

// Whether BEM keeps its own restorable copy of each Steam Workshop module a save uses. Off until the
// player turns it on, because the copies take disk space and a Workshop mod is the author's to withdraw.
public sealed record WorkshopKeepSettings(bool Enabled)
{
    public static WorkshopKeepSettings Default { get; } = new(false);
}

public sealed class WorkshopKeepSettingsStore(string? filePath = null)
{
    public const string FileName = "workshop-keep.json";

    public static string DefaultPath { get; } = Path.Combine(AppDataRoot.Folder, FileName);

    public string FilePath { get; } = filePath ?? DefaultPath;

    public WorkshopKeepSettings Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<WorkshopKeepSettings>(File.ReadAllText(FilePath)) ?? WorkshopKeepSettings.Default
                : WorkshopKeepSettings.Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return WorkshopKeepSettings.Default;
        }
    }

    public void Write(WorkshopKeepSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
