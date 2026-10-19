using System.Text.Json;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

// Off by default: a user who has not opted in gets the simpler, more predictable Auto-Sort
// behavior (every divider parks at the end - see LoadOrderDividerAutoSortPolicy), and the
// anchor-following behavior is one settings toggle away rather than a surprise.
public sealed record LoadOrderDividerOptions(bool KeepAnchoredOnAutoSort = false)
{
    public static LoadOrderDividerOptions Default { get; } = new();
}

// A shareable preference, deliberately its own small file - the same separation NexusOptionsStore
// already keeps between "flags" and other state.
public sealed class LoadOrderDividerOptionsStore(string filePath)
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string FilePath { get; } = filePath;

    public LoadOrderDividerOptions Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<LoadOrderDividerOptions>(File.ReadAllText(FilePath), Format)
                    ?? LoadOrderDividerOptions.Default
                : LoadOrderDividerOptions.Default;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return LoadOrderDividerOptions.Default;
        }
    }

    public void Save(LoadOrderDividerOptions options)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(options, Format));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}
