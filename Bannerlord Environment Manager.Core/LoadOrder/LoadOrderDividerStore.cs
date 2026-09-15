using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// The live view's current sections, kept in their own file next to (never inside) LauncherData.xml:
// that file is the actual game load order BUTR/BLSE read, and a divider id in it is a broken install
// the moment it is not backed by a real folder, which in this phase it never is.
public sealed class LoadOrderDividerStore(string filePath)
{
    private sealed record DividerFile(string Label, string? AnchorId, bool Collapsed, string? SourceModuleId);

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string FilePath { get; } = filePath;

    // Per version: this file describes one specific load order, so a machine-wide copy showed one
    // version's answer while another was selected. The resting version keeps the path it has always
    // used; every other version reads and writes its own.
    public static string GetDefaultPath(InstanceDataRoot? dataRoot = null) =>
        InstanceStateFolder.File(dataRoot, "dividers.json");

    public IReadOnlyList<LoadOrderDivider> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];

            var files = JsonSerializer.Deserialize<List<DividerFile>>(File.ReadAllText(FilePath), Format);

            return files is null
                ? []
                : [.. files.Select(f => new LoadOrderDivider(
                    f.Label,
                    f.AnchorId is null ? null : new ModuleId(f.AnchorId),
                    f.Collapsed,
                    f.SourceModuleId is null ? null : new ModuleId(f.SourceModuleId)))];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<LoadOrderDivider> dividers)
    {
        ArgumentNullException.ThrowIfNull(dividers);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            var files = dividers.Select(d => new DividerFile(
                d.Label, d.AnchorId?.Value, d.Collapsed, d.SourceModuleId?.Value));

            File.WriteAllText(FilePath, JsonSerializer.Serialize(files, Format));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}
