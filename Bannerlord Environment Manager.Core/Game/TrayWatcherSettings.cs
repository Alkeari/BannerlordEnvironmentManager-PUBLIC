using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Game;

/// <summary>
/// Whether BEM keeps a tray icon after its window closes and prepares every game launch, and whether the
/// one-time offer to turn that on has already been made. Off until the player turns it on.
/// </summary>
public sealed record TrayWatcherSettings(bool Enabled, bool OfferMade)
{
    public static TrayWatcherSettings Default { get; } = new(false, false);

    /// <summary>The offer is made once, for the first launch BEM did not start, and only while the watcher is off.</summary>
    public bool ShouldOffer(DetectedLaunch launch) => !Enabled && !OfferMade && !launch.StartedByBem;
}

public sealed class TrayWatcherSettingsStore(string? filePath = null)
{
    public const string FileName = "tray-watcher.json";

    public static string DefaultPath { get; } = Path.Combine(AppDataRoot.Folder, FileName);

    public string FilePath { get; } = filePath ?? DefaultPath;

    public TrayWatcherSettings Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<TrayWatcherSettings>(File.ReadAllText(FilePath)) ?? TrayWatcherSettings.Default
                : TrayWatcherSettings.Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return TrayWatcherSettings.Default;
        }
    }

    public void Write(TrayWatcherSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
