using System.Text.Json;

namespace BannerlordEnvironmentManager.Core.Toolkit;

// Off by default, and it stays that way: turning it on installs a third-party program on the user's
// machine without a further press, which is a thing to opt into rather than to discover afterwards.
public sealed record ToolkitOptions(bool InstallSevenZipOnDemand = false)
{
    public static ToolkitOptions Default { get; } = new();
}

// Its own small file next to the toolkit ledger, the same separation every other preference here keeps.
public sealed class ToolkitOptionsStore(string filePath)
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        "toolkit-settings.json");

    public string FilePath { get; } = filePath;

    public ToolkitOptions Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<ToolkitOptions>(File.ReadAllText(FilePath), Format)
                    ?? ToolkitOptions.Default
                : ToolkitOptions.Default;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // An unreadable file reads as off, which is the setting that changes nothing on the machine.
            _ = ex;

            return ToolkitOptions.Default;
        }
    }

    public void Save(ToolkitOptions options)
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
