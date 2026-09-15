using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BannerlordEnvironmentManager.Core.Install;

// A folder's cheap signature is the ordered set of its files' names, lengths and last-write times.
// Computing it reads directory metadata only, never file contents, so it is two or three orders of
// magnitude cheaper than hashing every file. A signature match means the folder's file set is byte-
// for-byte the same size and timestamps as the last time it was fully fingerprinted, so the recorded
// fingerprint is still the fingerprint of what is on disk.
//
// The cache lives on disk so a BEM restart does not pay to re-hash a folder that has not changed
// since the last session. It is keyed by folder path and written under a lock, because the update
// verdict path fingerprints several modules concurrently on worker threads.
public sealed class ModuleFingerprintCache
{
    private readonly string filePath;
    private readonly Dictionary<string, Entry> entries;
    private readonly object gate = new();

    private sealed record Entry(string Signature, string Fingerprint);

    public ModuleFingerprintCache(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        this.filePath = filePath;
        entries = Load();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        "module-fingerprint-cache.json");

    // Recomputes the metadata signature of a folder and, when it has not changed since the last full
    // fingerprint, returns the recorded fingerprint without reading a single file's contents. Null
    // means the folder could not be described (gone, unreadable) - not that it has no changes.
    public string? Recall(string moduleFolderPath, Func<string?> computeFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleFolderPath);

        var signature = Signature(moduleFolderPath);

        if (signature is null)
            return null;

        lock (gate)
        {
            if (entries.TryGetValue(moduleFolderPath, out var cached) && cached.Signature == signature)
                return cached.Fingerprint;
        }

        var fingerprint = computeFingerprint();

        if (fingerprint is null)
            return null;

        lock (gate)
        {
            entries[moduleFolderPath] = new Entry(signature, fingerprint);
            Save();
        }

        return fingerprint;
    }

    // Recall without the fallback: the fingerprint already held for a folder whose metadata still
    // matches, and null the moment computing one would mean reading files. Windows refuses to rename
    // a folder while any file inside it is open, so a caller that is only inspecting a folder
    // somebody else may be building into asks this and accepts not knowing. The signature itself is
    // safe to compute: it reads directory metadata and opens nothing.
    public string? RecallHeld(string moduleFolderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleFolderPath);

        if (Signature(moduleFolderPath) is not { } signature)
            return null;

        lock (gate)
        {
            return entries.TryGetValue(moduleFolderPath, out var cached) && cached.Signature == signature
                ? cached.Fingerprint
                : null;
        }
    }

    private static string? Signature(string moduleFolderPath)
    {
        if (string.IsNullOrWhiteSpace(moduleFolderPath) || !Directory.Exists(moduleFolderPath))
            return null;

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(moduleFolderPath));

            // Listed to the end before a single FileInfo is asked anything. Windows refuses to rename a
            // folder while an enumeration of it is still open, and leaving the query lazy held that
            // enumeration open across the whole metadata pass, so a folder BEM was only signing was a
            // folder a mod build could not deploy into for as long as the pass took.
            var paths = Directory.GetFiles(root, "*", SearchOption.AllDirectories);

            var files = paths
                .Where(path => InstalledModuleContents.Counted(root, path))
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return $"{InstalledModuleContents.RelativeForSignature(root, path)}\n{info.Length.ToString(CultureInfo.InvariantCulture)}\n{info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}\n";
                })
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToList();

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            foreach (var line in files)
                hash.AppendData(Encoding.UTF8.GetBytes(line));

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private Dictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(filePath)
                && JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(filePath)) is { } loaded)
                return loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temp = filePath + ".tmp";

            File.WriteAllText(temp, JsonSerializer.Serialize(entries));
            File.Move(temp, filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}