using System.Security.Cryptography;
using System.Text.Json;
using BannerlordEnvironmentManager.Core.Io;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Saves;

namespace BannerlordEnvironmentManager.Core.Modules;

public enum WorkshopKeepOutcome
{
    // The setting is off, or the module did not come from the Workshop, or its folder is gone.
    NotKept,
    Copied,
    AlreadyKept
}

// One module held in the store, as recorded when it was copied.
public sealed record KeptWorkshopModule(string ModuleId, string Name, string VersionText, string Hash, string SourceFolder, string StoredFolder);

// A restorable copy of each subscribed Steam Workshop module, content-hashed under the BEM data root:
// <root>\<ModuleId>\<hash>\module holds the files and <root>\<ModuleId>\<hash>\kept.json says where they
// came from. A changed module hashes differently and so lands beside the older copy rather than over it.
// The Workshop folder is only ever read: a copy is built beside its final place, proved to hold every
// byte of the source, and only then renamed in, and a restore does the same into the game's Modules.
public sealed class WorkshopKeep(string root, Func<bool> isEnabled)
{
    private const string ModuleFolder = "module";
    private const string RecordFile = "kept.json";

    public string Root { get; } = root;

    public bool IsEnabled => isEnabled();

    public WorkshopKeepOutcome Keep(ModuleManifest module)
    {
        if (!isEnabled() || !WorkshopContent.CameFromWorkshop(module) || !Directory.Exists(module.FolderPath))
            return WorkshopKeepOutcome.NotKept;

        var hash = HashFolder(module.FolderPath);
        var entry = Path.Combine(Root, Safe(module.Id.Value), hash);

        if (File.Exists(Path.Combine(entry, RecordFile)))
            return WorkshopKeepOutcome.AlreadyKept;

        var staging = entry + ".partial";
        DirectoryMover.TryDeleteTree(staging);

        try
        {
            DirectoryMover.Copy(module.FolderPath, Path.Combine(staging, ModuleFolder));

            if (!DirectoryContents.IsCoveredBy(module.FolderPath, Path.Combine(staging, ModuleFolder)))
                throw new IOException(Strings.Current.Format("Core.Modules.WorkshopKeep.IncompleteCopy", module.FolderPath));

            var record = new KeptWorkshopModule(
                module.Id.Value, module.Name, module.VersionText ?? module.Version.ToString(), hash, module.FolderPath, "");
            File.WriteAllText(Path.Combine(staging, RecordFile), JsonSerializer.Serialize(record));

            if (Directory.Exists(entry))
                DirectoryMover.TryDeleteTree(entry);

            Directory.Move(staging, entry);
        }
        catch
        {
            DirectoryMover.TryDeleteTree(staging);
            throw;
        }

        return WorkshopKeepOutcome.Copied;
    }

    // Keeps every module the save lists that is installed from the Workshop.
    public int KeepForSave(SaveFile save, IEnumerable<ModuleManifest> installed)
    {
        var wanted = save.Modules.Select(m => m.Id).ToHashSet();
        var copied = 0;

        foreach (var module in installed.Where(m => wanted.Contains(m.Id)))
        {
            if (Keep(module) == WorkshopKeepOutcome.Copied)
                copied++;
        }

        return copied;
    }

    public IReadOnlyList<KeptWorkshopModule> Kept()
    {
        if (!Directory.Exists(Root))
            return [];

        var kept = new List<KeptWorkshopModule>();

        foreach (var record in Directory.EnumerateFiles(Root, RecordFile, SearchOption.AllDirectories))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<KeptWorkshopModule>(File.ReadAllText(record));

                if (entry is not null)
                    kept.Add(entry with { StoredFolder = Path.Combine(Path.GetDirectoryName(record)!, ModuleFolder) });
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
            }
        }

        return kept;
    }

    // The newest copy of each stored module whose Workshop folder is gone and which is not installed
    // under the game's Modules folder, so is the one a player has lost.
    public IReadOnlyList<KeptWorkshopModule> Missing(string gameModulesFolder) =>
        [.. Kept()
            .Where(k => !Directory.Exists(k.SourceFolder)
                && !Directory.Exists(Path.Combine(gameModulesFolder, k.ModuleId)))
            .GroupBy(k => k.ModuleId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(k => Directory.GetCreationTimeUtc(k.StoredFolder)).First())];

    // Copies the stored module into Modules\<ModuleId> as an ordinary local module.
    public string Restore(KeptWorkshopModule kept, string gameModulesFolder)
    {
        var destination = Path.Combine(gameModulesFolder, kept.ModuleId);

        if (Directory.Exists(destination))
            throw new IOException(Strings.Current.Format("Core.Modules.WorkshopKeep.DestinationExists", destination));

        var staging = destination + ".restoring";
        DirectoryMover.TryDeleteTree(staging);

        try
        {
            DirectoryMover.Copy(kept.StoredFolder, staging);

            if (!DirectoryContents.IsCoveredBy(kept.StoredFolder, staging))
                throw new IOException(Strings.Current.Format("Core.Modules.WorkshopKeep.IncompleteCopy", kept.StoredFolder));

            Directory.Move(staging, destination);
        }
        catch
        {
            DirectoryMover.TryDeleteTree(staging);
            throw;
        }

        return destination;
    }

    // Names and bytes of every file, in path order, links left out as the copy leaves them out.
    private static string HashFolder(string folder)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                     .Where(f => !File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint))
                     .OrderBy(f => Path.GetRelativePath(folder, f), StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(folder, file).Replace('\\', '/') + "\n"));
            hash.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexString(hash.GetHashAndReset())[..32].ToLowerInvariant();
    }

    private static string Safe(string id) =>
        string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
