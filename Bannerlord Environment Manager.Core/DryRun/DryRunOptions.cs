using System.Text.Json;
using System.Text.Json.Serialization;

namespace BannerlordEnvironmentManager.Core.DryRun;

// How long a boot check waits for the game before calling it stuck. Six minutes was the fixed limit
// before it became a setting, and a large overhaul can need more: Tales from the Age of Men had
// loaded 7 of its 17 modules when six minutes ran out on 2026-10-15.
public sealed record DryRunOptions(int TimeLimitMinutes = DryRunOptions.DefaultTimeLimitMinutes)
{
    public const int DefaultTimeLimitMinutes = 6;

    public const int MinimumTimeLimitMinutes = 1;

    public const int MaximumTimeLimitMinutes = 60;

    public static DryRunOptions Default { get; } = new();

    [JsonIgnore]
    public TimeSpan TimeLimit => TimeSpan.FromMinutes(
        Math.Clamp(TimeLimitMinutes, MinimumTimeLimitMinutes, MaximumTimeLimitMinutes));
}

public sealed class DryRunOptionsStore(string filePath)
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        "boot-check-settings.json");

    public string FilePath { get; } = filePath;

    public DryRunOptions Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<DryRunOptions>(File.ReadAllText(FilePath), Format)
                    ?? DryRunOptions.Default
                : DryRunOptions.Default;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            _ = ex;

            return DryRunOptions.Default;
        }
    }

    public void Save(DryRunOptions options)
    {
        try
        {
            var folder = Path.GetDirectoryName(FilePath);

            if (!string.IsNullOrWhiteSpace(folder))
                Directory.CreateDirectory(folder);

            File.WriteAllText(FilePath, JsonSerializer.Serialize(options, Format));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _ = ex;
        }
    }
}
