namespace BannerlordEnvironmentManager.Core.Interop;

public enum LoadOrderFileFormat
{
    BmList,
    NovusPreset,
    MtOrder,
    Text
}

// IsEnabled is true for every entry of a format that has no way to say otherwise. Only .mtorder
// carries a real flag, and only there can this be false.
public sealed record LoadOrderFileEntry(
    string Id,
    string Version = "",
    bool IsEnabled = true,
    string? Name = null,
    string? Url = null);

public sealed record LoadOrderFileRead(
    IReadOnlyList<LoadOrderFileEntry> Entries,
    IReadOnlyList<string> DuplicateIds,
    int UnreadableLines = 0,
    string? Error = null)
{
    public bool Failed => Error is not null;

    public static LoadOrderFileRead Unreadable(string error) => new([], [], 0, error);
}

public sealed record LoadOrderFileWrite(string Content, IReadOnlyList<string> Unrepresentable);
