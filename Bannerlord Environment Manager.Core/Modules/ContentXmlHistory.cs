using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Modules;

// What one check found against the check before it. A total alone cannot show whether a repair worked
// or what a changed mod did, so every file lands in exactly one list. Removed is a file that failed
// last time and no longer exists, which is not a fix. HasPrevious is false on the first check.
public sealed record ContentXmlComparison(
    bool HasPrevious,
    IReadOnlyList<string> New,
    IReadOnlyList<string> Fixed,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Still);

// The files that failed the last check on one version, as full paths.
public sealed class ContentXmlHistory
{
    public const string FileName = "content-xml-last-check.json";

    public ContentXmlHistory(string? filePath) => FilePath = filePath;

    public string? FilePath { get; }

    public static string DefaultPath(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, FileName);

    public static ContentXmlComparison Compare(
        IReadOnlyCollection<string>? previous, IReadOnlyCollection<string> current, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(fileExists);

        var now = current.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

        if (previous is null)
            return new ContentXmlComparison(false, [], [], [], now);

        var before = previous.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nowSet = now.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = before.Where(file => !nowSet.Contains(file)).Order(StringComparer.OrdinalIgnoreCase).ToList();

        return new ContentXmlComparison(
            true,
            [.. now.Where(file => !before.Contains(file))],
            [.. gone.Where(fileExists)],
            [.. gone.Where(file => !fileExists(file))],
            [.. now.Where(before.Contains)]);
    }

    // Null when no check has been recorded or the record cannot be read, so nothing is claimed against it.
    public IReadOnlyList<string>? Load()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            return null;

        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Save(IEnumerable<string> failing)
    {
        ArgumentNullException.ThrowIfNull(failing);

        if (string.IsNullOrWhiteSpace(FilePath))
            return false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(failing.ToList()));
            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
