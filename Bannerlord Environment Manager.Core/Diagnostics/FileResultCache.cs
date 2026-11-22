using System.Collections.Concurrent;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// Remembers what a pure read of one file produced, for as long as the process lives, keyed by the file's
// path, size and last write time. Launch scans the same few hundred assemblies each time it is pressed
// and almost none of them changed since the last press, so the second press reads nothing it has seen.
// A file that changes, even in place, has a new key and is read again.
internal sealed class FileResultCache<T>
{
    private readonly ConcurrentDictionary<(string Path, long Length, long Ticks), T> _results = new();

    public T GetOrRead(string path, Func<T> read)
    {
        (string, long, long) key;

        try
        {
            var info = new FileInfo(path);
            key = (path.ToLowerInvariant(), info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return read();
        }

        // A read that throws is not remembered, so the caller sees the same exception it always did.
        if (_results.TryGetValue(key, out var cached))
            return cached;

        return _results[key] = read();
    }
}
