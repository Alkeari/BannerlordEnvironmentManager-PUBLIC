using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Settings;

public sealed record SectionLayout(double Size, bool Collapsed);

// How big each adjustable section was left and whether it was collapsed, kept across sessions. A file
// that cannot be read means defaults, never an error: this is only ever a convenience.
public sealed class SectionLayoutStore(string? filePath = null)
{
    public const string FileName = "section-layout.json";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string FilePath { get; } = filePath ?? Path.Combine(AppDataRoot.Folder, FileName);

    public SectionLayout? Read(string key) => ReadAll().GetValueOrDefault(key);

    public void Write(string key, SectionLayout layout)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(layout);

        var all = ReadAll();
        all[key] = layout;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, Format));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The section still works at the size he set; only remembering it failed.
        }
    }

    // Back to every default: each section and splitter reads nothing and takes the size it was built with.
    public bool Reset()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private Dictionary<string, SectionLayout> ReadAll()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, SectionLayout>>(File.ReadAllText(FilePath), Format) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
