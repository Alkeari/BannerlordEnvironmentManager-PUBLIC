using System.Text.Json;
using BannerlordEnvironmentManager.Core.Game;

namespace BannerlordEnvironmentManager.Core.Install;

// One copy BEM made so a module that ships binaries only for the other PC platform loads here.
public sealed record AdoptedPlatformRecord(
    string Id,
    string ModuleFolderName,
    string ModuleFolderPath,
    string SourcePath,
    string TargetPath,
    string PlatformFolder,
    string TargetPlatformFolder,
    long SizeBytes,
    int FileCount,
    DateTimeOffset AdoptedUtc,
    bool Undone);

// What launch-time adoption keeps so it can be put back from the Undo list. Undo deletes only the copy
// BEM made (the shipped folder is never touched) and keeps the record marked undone, so the next launch
// does not make the same copy again.
public sealed class PlatformAdoptionLog
{
    public const string FileName = "adopted-platform.json";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private readonly string path;

    public PlatformAdoptionLog(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        path = Path.Combine(rootPath, FileName);
    }

    public IReadOnlyList<AdoptedPlatformRecord> Read()
    {
        if (!File.Exists(path))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<AdoptedPlatformRecord>>(File.ReadAllText(path), Format) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public IReadOnlyList<AdoptedPlatformRecord> Active() => [.. Read().Where(record => !record.Undone)];

    // True when the owner already undid an adoption into this folder, so it is left alone.
    public bool WasUndone(string targetPath) => Read().Any(record =>
        record.Undone && string.Equals(record.TargetPath, targetPath, StringComparison.OrdinalIgnoreCase));

    public void Record(IEnumerable<AdoptablePlatformFolder> sources, PlatformAdoptionResult result)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(result);

        var made = new List<AdoptedPlatformRecord>();

        foreach (var created in result.Created)
        {
            var source = sources.FirstOrDefault(item =>
                string.Equals(item.TargetPath, created.Path, StringComparison.OrdinalIgnoreCase));

            made.Add(new AdoptedPlatformRecord(
                Guid.NewGuid().ToString("N"), created.ModuleFolderName, created.ModuleFolderPath,
                source?.Path ?? string.Empty, created.Path, source?.PlatformFolder ?? string.Empty,
                created.PlatformFolder, created.SizeBytes, created.FileCount, DateTimeOffset.UtcNow, false));
        }

        if (made.Count == 0)
            return;

        // A copy made again into a folder that was undone replaces that record rather than stacking.
        var kept = Read().Where(record => !made.Any(item =>
            string.Equals(item.TargetPath, record.TargetPath, StringComparison.OrdinalIgnoreCase)));

        Write([.. kept, .. made]);
    }

    // Deletes the copy BEM made and nothing else. Refuses a path that is not inside its module folder or
    // not named for a platform, so a hand-edited log cannot point it elsewhere.
    public bool Undo(string id)
    {
        var records = Read();
        var record = records.FirstOrDefault(item => item.Id == id && !item.Undone);

        if (record is null)
            return false;

        var target = Path.GetFullPath(record.TargetPath);
        var module = Path.GetFullPath(record.ModuleFolderPath).TrimEnd((char)92, '/') + Path.DirectorySeparatorChar;

        if (!target.StartsWith(module, StringComparison.OrdinalIgnoreCase)
            || !PlatformBinaries.IsKnownPlatformFolder(Path.GetFileName(target.TrimEnd((char)92, '/'))))
        {
            return false;
        }

        try
        {
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        Write([.. records.Select(item => item.Id == id ? item with { Undone = true } : item)]);

        return true;
    }

    private void Write(IReadOnlyList<AdoptedPlatformRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(records, Format));
    }
}
