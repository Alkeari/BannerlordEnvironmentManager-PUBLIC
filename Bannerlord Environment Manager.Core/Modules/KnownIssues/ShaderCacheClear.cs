using BannerlordEnvironmentManager.Core.Diagnostics;

namespace BannerlordEnvironmentManager.Core.Modules.KnownIssues;

// The fix for a stale module shader cache (findings catalog K06, G5): every file of the module's own
// shaders folders moves into the cleared-file store as one batch, so the game rebuilds the cache on the
// next launch and Undo puts the files back exactly where they were. Nothing is deleted. Engine-owned
// caches are never touched (ShaderFolders already leaves them out).
public static class ShaderCacheClear
{
    public static IReadOnlyList<ClearCandidate> Candidates(string moduleFolder)
    {
        var files = new List<ClearCandidate>();

        foreach (var folder in ShaderFolders.FindIn(moduleFolder))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories))
                    files.Add(new ClearCandidate(file, new FileInfo(file).Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder BEM cannot read is left where it is; Clear only reports what it moved.
            }
        }

        return files;
    }

    public static bool HasCache(string moduleFolder) => Candidates(moduleFolder).Count > 0;

    // Moves are confined to the module's own folder, whatever the candidates say.
    public static ClearResult Clear(ClearedFileStore store, string moduleFolder, string reason, DateTimeOffset stamp)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleFolder);

        return store.Clear(Candidates(moduleFolder), [moduleFolder], reason, stamp);
    }

    public static RestoreResult Undo(ClearedFileStore store, ClearResult cleared, DateTimeOffset stamp)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cleared);

        return store.Restore(cleared.BatchPath, stamp);
    }
}
