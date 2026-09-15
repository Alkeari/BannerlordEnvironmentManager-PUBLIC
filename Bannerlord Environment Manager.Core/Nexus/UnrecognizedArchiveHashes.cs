using System.Text.Json;
using System.Text.Json.Serialization;

namespace BannerlordEnvironmentManager.Core.Nexus;

// A hash Nexus answered 404 to, and when it said so. Nexus returns 404 for bytes it does not publish
// for this game, which happens for a local repack, an archive from somewhere else, and a file whose
// page has been hidden. Without this the same 404 is collected again on every press, against an
// allowance shared with whatever other mod manager the user is signed in to.
//
// It is a saving and never a verdict. "These exact bytes are not a published file today" is not "this
// mod is not on Nexus", so nothing here ever reaches not-on-nexus.json and nothing here is shown as an
// answer about a module.
public sealed record UnrecognizedArchiveHash(string Md5, DateTimeOffset RecordedUtc);

// Failed is what the file would have held if the write had worked, so a save that never reached the
// disk cannot be reported as one that did.
public sealed record UnrecognizedHashRecord(int Added, int Unchanged, int Removed, int Failed = 0);

// Machine-wide rather than per version, because a hash is a statement about bytes and the same archive
// installed into two instances is the same bytes. It sits beside nexus-mod-versions.json, which is the
// other file holding what Nexus answered rather than what the user decided.
public sealed class UnrecognizedArchiveHashStore
{
    public const string FileName = "unrecognized-archive-hashes.json";

    // A hash Nexus does not know today is one it may know next week: the author re-uploads, or a hidden
    // page comes back. A memory that never expired would silently stop BEM ever identifying that
    // archive again, which is a worse defect than the one it fixes. The window is short on purpose. It
    // only has to outlive a press, a session and the few days somebody spends sorting out a load order,
    // and re-asking once a week costs one request per unrecognized hash against an allowance of twenty
    // thousand a day, so nothing is bought by holding the no for longer.
    public static TimeSpan Forgets { get; } = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public UnrecognizedArchiveHashStore(string? filePath) => FilePath = filePath;

    public static UnrecognizedArchiveHashStore Default { get; } = new(DefaultPath());

    // For a caller that wants nothing this machine happens to hold, and for tests.
    public static UnrecognizedArchiveHashStore None { get; } = new((string?)null);

    public string? FilePath { get; }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        FileName);

    public IReadOnlyList<UnrecognizedArchiveHash> Load()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            return [];

        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<UnrecognizedArchiveHash>>(File.ReadAllText(FilePath), Format) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    // Only the hashes still inside the window. An expired one is left on disk until the next write
    // rather than deleted on a read, so reading the file never changes it.
    public IReadOnlySet<string> Remembered(DateTimeOffset now) =>
        Load()
            .Where(hash => !HasExpired(hash, now))
            .Select(hash => Normalize(hash.Md5))
            .Where(md5 => md5.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public UnrecognizedHashRecord Record(IEnumerable<string> hashes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hashes);

        var incoming = hashes.Select(Normalize).Where(md5 => md5.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (string.IsNullOrWhiteSpace(FilePath) || incoming.Count == 0)
            return new UnrecognizedHashRecord(0, 0, 0);

        var existing = Load()
            .Where(hash => !HasExpired(hash, now))
            .ToDictionary(hash => Normalize(hash.Md5), StringComparer.OrdinalIgnoreCase);

        var expired = Load().Count - existing.Count;
        var added = 0;
        var unchanged = 0;

        foreach (var md5 in incoming)
        {
            if (existing.ContainsKey(md5))
            {
                unchanged++;
                continue;
            }

            existing[md5] = new UnrecognizedArchiveHash(md5, now);
            added++;
        }

        if ((added > 0 || expired > 0) && !Write(Ordered(existing.Values)))
            return new UnrecognizedHashRecord(0, 0, 0, added);

        return new UnrecognizedHashRecord(added, unchanged, expired);
    }

    // The other direction, so a hash BEM stopped asking about is one the user can put back in the
    // queue. A no BEM could record and nobody could take back would be the permanent no this file is
    // built to avoid.
    public UnrecognizedHashRecord Forget(IEnumerable<string> hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);

        var drop = hashes.Select(Normalize).Where(md5 => md5.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return drop.Count == 0
            ? new UnrecognizedHashRecord(0, 0, 0)
            : Drop(hash => drop.Contains(Normalize(hash.Md5)));
    }

    public UnrecognizedHashRecord ForgetAll() => Drop(_ => true);

    private UnrecognizedHashRecord Drop(Func<UnrecognizedArchiveHash, bool> matches)
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            return new UnrecognizedHashRecord(0, 0, 0);

        var all = Load();
        var kept = all.Where(hash => !matches(hash)).ToList();
        var removed = all.Count - kept.Count;

        if (removed > 0 && !Write(Ordered(kept)))
            return new UnrecognizedHashRecord(0, 0, 0, removed);

        return new UnrecognizedHashRecord(0, 0, removed);
    }

    private static bool HasExpired(UnrecognizedArchiveHash hash, DateTimeOffset now) =>
        now - hash.RecordedUtc >= Forgets;

    private static string Normalize(string? md5) => (md5 ?? string.Empty).Trim().ToLowerInvariant();

    private static List<UnrecognizedArchiveHash> Ordered(IEnumerable<UnrecognizedArchiveHash> hashes) =>
        [.. hashes.OrderBy(hash => hash.Md5, StringComparer.Ordinal)];

    // Written beside the real file and moved over it, so a write that stops halfway leaves what was
    // already remembered intact rather than truncated. False means nothing on disk changed.
    private bool Write(IReadOnlyList<UnrecognizedArchiveHash> hashes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath!)!);

            var pending = FilePath + ".tmp";

            File.WriteAllText(pending, JsonSerializer.Serialize(hashes, Format));
            File.Move(pending, FilePath!, overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
