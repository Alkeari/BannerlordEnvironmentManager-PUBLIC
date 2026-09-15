using System.Text.Json;

namespace BannerlordEnvironmentManager.Core.Localization;

public sealed record LanguagePreference(string? Code)
{
    public static readonly LanguagePreference None = new((string?)null);
}

public sealed class LanguageSettingsStore(string? filePath = null)
{
    public const string FileName = "language.json";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        FileName);

    public string FilePath { get; } = filePath ?? DefaultPath;

    public LanguagePreference Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<LanguagePreference>(File.ReadAllText(FilePath), Format)
                  ?? LanguagePreference.None
                : LanguagePreference.None;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return LanguagePreference.None;
        }
    }

    public void Write(LanguagePreference preference)
    {
        ArgumentNullException.ThrowIfNull(preference);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(preference, Format));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
