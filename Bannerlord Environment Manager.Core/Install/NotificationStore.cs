using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.Install;

public enum NotificationKind
{
    DownloadFinished,
    DownloadFailed,
    UpdateInstalled,
    UpdateFailed
}

// One line of the notifications list: something BEM finished while the owner was elsewhere.
public sealed record NotificationEntry(
    DateTime WhenUtc,
    NotificationKind Kind,
    string Title,
    string Detail,
    bool IsRead = false);

// A persistent, capped list of finished downloads and updates, as Vortex's notifications feed shows
// them. It belongs to the whole app rather than one game version, because a download finishes for the
// owner, not for an instance.
public sealed class NotificationStore(string? filePath, int capacity = NotificationStore.DefaultCapacity)
{
    public const string FileName = "notifications.json";

    public const int DefaultCapacity = 100;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private readonly Lock gate = new();

    public string? FilePath { get; } = filePath;

    public static string DefaultPath() => Path.Combine(AppDataRoot.Folder, FileName);

    // Newest first.
    public IReadOnlyList<NotificationEntry> Load()
    {
        lock (gate)
            return ReadAll();
    }

    public bool Add(NotificationEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Title);

        lock (gate)
            return WriteAll([.. ReadAll().Prepend(entry).OrderByDescending(item => item.WhenUtc).Take(Math.Max(1, capacity))]);
    }

    public bool MarkAllRead()
    {
        lock (gate)
            return WriteAll([.. ReadAll().Select(entry => entry with { IsRead = true })]);
    }

    public bool Clear()
    {
        lock (gate)
            return WriteAll([]);
    }

    private List<NotificationEntry> ReadAll()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
            return [];

        try
        {
            var stored = JsonSerializer.Deserialize<List<NotificationEntry>>(File.ReadAllText(FilePath)) ?? [];

            return [.. stored.Where(entry => !string.IsNullOrWhiteSpace(entry.Title)).OrderByDescending(entry => entry.WhenUtc)];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private bool WriteAll(List<NotificationEntry> all)
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            return false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            var temporary = FilePath + ".tmp";

            File.WriteAllText(temporary, JsonSerializer.Serialize(all, Format));
            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
