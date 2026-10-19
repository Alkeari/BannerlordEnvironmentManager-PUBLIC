using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BannerlordEnvironmentManager.Core.Install;

// Everything identifying about a mod archive that stays true once the archive itself is gone. Most
// users have delete-after-install on, so the moment an archive is installed is the only moment any of
// this can be read, and it is read the same way whether the file arrived through a nxm link or was
// downloaded by hand from the website.
public sealed record ArchiveIdentity(
    string FileName,
    long? SizeBytes = null,
    string? Md5 = null,
    DateTimeOffset? ModifiedUtc = null,
    int? NexusModId = null,
    int? NexusFileId = null,
    string? NexusFileName = null,
    string? NexusVersion = null,
    DateTimeOffset? NexusUploadedUtc = null,
    DateTimeOffset? ObservedUtc = null)
{
    public ArchiveIdentity Fill(ArchiveIdentity? other) =>
        other is null
            ? this
            : this with
            {
                SizeBytes = SizeBytes ?? other.SizeBytes,
                Md5 = Md5 ?? other.Md5,
                ModifiedUtc = ModifiedUtc ?? other.ModifiedUtc,
                NexusModId = NexusModId ?? other.NexusModId,
                NexusFileId = NexusFileId ?? other.NexusFileId,
                NexusFileName = NexusFileName ?? other.NexusFileName,
                NexusVersion = NexusVersion ?? other.NexusVersion,
                NexusUploadedUtc = NexusUploadedUtc ?? other.NexusUploadedUtc,
                ObservedUtc = ObservedUtc ?? other.ObservedUtc
            };
}

public static class ArchiveIdentityReader
{
    // MD5 rather than something stronger, because this hash exists to be asked a question with. Nexus
    // publishes exactly one lookup keyed on file content, md5_search, and it is keyed on MD5. A
    // stronger digest would be a number nobody can answer anything about, and the threat model here is
    // an author renaming a file, not an adversary forging one.
    // The mod and file ids are deliberately left unset. They are readable from the file name, and the
    // record that keeps this reads them there on every load, so that a better parser tomorrow recovers
    // more than today's did. Only a source that states them outright fills them in.
    public static ArchiveIdentity Read(string archivePath, DateTimeOffset observedUtc, bool hash = true)
    {
        var fileName = Path.GetFileName(archivePath);

        long? size = null;
        DateTimeOffset? modified = null;

        try
        {
            var file = new FileInfo(archivePath);

            if (file.Exists)
            {
                size = file.Length;
                modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }

        return new ArchiveIdentity(
            fileName,
            size,
            hash ? TryHash(archivePath) : null,
            modified,
            NexusModId: null,
            NexusFileId: null,
            NexusFileName: null,
            NexusVersion: null,
            NexusUploadedUtc: null,
            observedUtc);
    }

    // A hash that could not be taken is absent, never a placeholder: a wrong hash asks Nexus about
    // somebody else's file.
    public static string? TryHash(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);

            return Convert.ToHexStringLower(MD5.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}

// What BEM knew about a download at the moment it fetched it, keyed by the name the file landed
// under. A nxm link states the mod id and the file id outright, which no filename grammar is obliged
// to carry, so recording them here means the install that follows knows them whether or not Nexus's
// naming survives the trip. Nothing depends on this file: a manual download simply has no entry, and
// the install reads what the filename and the bytes say instead.
public sealed class ArchiveProvenanceStore
{
    public const string FileName = "archive-provenance.json";

    // Enough to cover any plausible run of downloads before installing, and small enough that the file
    // never becomes something to manage.
    private const int Keep = 200;

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ArchiveProvenanceStore(string? filePath) => FilePath = filePath;

    public static ArchiveProvenanceStore Default { get; } = new(DefaultPath());

    public static ArchiveProvenanceStore None { get; } = new((string?)null);

    public string? FilePath { get; }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        FileName);

    public IReadOnlyList<ArchiveIdentity> Load()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            return [];

        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<ArchiveIdentity>>(File.ReadAllText(FilePath), Format) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    public ArchiveIdentity? Find(string fileName) =>
        string.IsNullOrWhiteSpace(fileName)
            ? null
            : Load()
                .Where(entry => string.Equals(entry.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.ObservedUtc ?? DateTimeOffset.MinValue)
                .FirstOrDefault();

    public bool Record(ArchiveIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (string.IsNullOrWhiteSpace(FilePath) || string.IsNullOrWhiteSpace(identity.FileName))
            return false;

        var kept = Load()
            .Where(entry => !string.Equals(entry.FileName, identity.FileName, StringComparison.OrdinalIgnoreCase))
            .Append(identity)
            .OrderByDescending(entry => entry.ObservedUtc ?? DateTimeOffset.MinValue)
            .Take(Keep)
            .ToList();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(kept, Format));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
