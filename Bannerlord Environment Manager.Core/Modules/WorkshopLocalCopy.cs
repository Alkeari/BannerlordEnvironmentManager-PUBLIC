using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BannerlordEnvironmentManager.Core.Io;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Modules;

public enum WorkshopCopyState
{
    // A Workshop module with no copy in Modules.
    NotCopied,
    // A copy BEM made sits in Modules and matches the Workshop version it was made from (or the Workshop
    // folder cannot be judged, which is never reported as a problem).
    Copied,
    // The Workshop item changed since the copy was made and the copy is as BEM left it, so a press refreshes it.
    WorkshopUpdated,
    // The Workshop item changed, but the copy was edited after BEM made it, so it is warned about and never overwritten.
    WorkshopUpdatedCopyEdited,
    // Modules already holds a module with this id that BEM did not copy from this item, so nothing is copied.
    Conflict,
    // Steam's own record no longer lists the item at all, and the copy BEM made is what is left.
    RemovedFromSubscriptions,
    // Steam's own record still lists the item but its folder is not on disk, and the copy is what is left.
    WorkshopFolderMissing
}

public sealed record WorkshopCopyItem(
    string ItemId,
    string ModuleId,
    string Name,
    // Null when the Workshop folder is gone and only the copy remains.
    string? SourceFolder,
    // Null while there is no copy.
    string? CopyFolder,
    WorkshopCopyState State)
{
    public bool NeedsCopy => State is WorkshopCopyState.NotCopied or WorkshopCopyState.WorkshopUpdated;

    public bool IsFlagged => State is WorkshopCopyState.RemovedFromSubscriptions
        or WorkshopCopyState.WorkshopFolderMissing
        or WorkshopCopyState.WorkshopUpdatedCopyEdited
        or WorkshopCopyState.Conflict;
}

public enum WorkshopCopyOutcome
{
    Copied,
    Refreshed,
    // Nothing to do or nothing safe to do; Message says why.
    Skipped,
    Failed
}

public sealed record WorkshopCopyResult(WorkshopCopyItem Item, WorkshopCopyOutcome Outcome, string Message, string? CopyFolder = null);

// What BEM writes into a copy so it can tell its own copy from a mod the player installed by hand.
public sealed record WorkshopCopyRecord(
    string ItemId, string ModuleId, string SourceFolder, string SourceFingerprint, string CopyFingerprint, DateTime CopiedUtc);

// A copy of a subscribed Steam Workshop module in the game's Modules folder, so the mod stays installed
// when its author or Steam takes the item down. The Workshop folder is only ever read: the copy is
// built outside Modules (a half-built copy there would be a second module with the same id and stop the
// game), proved to hold every byte of the source, stamped with a record, and renamed in only then.
//
// The record is a file inside the copy, not a database, so it moves with the folder and a copy the
// player deletes or renames by hand can never leave a stale entry behind. A module folder without that
// file is the player's own and is never overwritten.
public static class WorkshopLocalCopy
{
    public const string RecordFileName = ".bem-workshop-copy.json";
    private const string StagingFolder = ".bem-workshop-staging";

    // The item id of the Workshop folder a Modules folder was copied from, or null for any other folder.
    public static string? CopiedItemId(string moduleFolder) => ReadRecord(moduleFolder)?.ItemId;

    // Whether localFolder is BEM's copy of the Workshop item in workshopFolder. The scanner uses this to
    // stay silent about the pair: the Workshop original is the same module, not a second one.
    public static bool IsCopyOf(string localFolder, string workshopFolder)
    {
        var id = ModulePageUrl.WorkshopIdOfFolder(workshopFolder);
        return id is not null && string.Equals(CopiedItemId(localFolder), id, StringComparison.Ordinal);
    }

    public static IReadOnlyList<WorkshopCopyItem> Survey(string gameInstallPath)
    {
        var workshopContent = ModuleScanner.GetWorkshopFolder(gameInstallPath);

        if (workshopContent is null)
            return [];

        var modulesFolder = ModuleScanner.GetModulesFolder(gameInstallPath);
        RecoverPrevious(gameInstallPath, modulesFolder);
        var local = Directory.Exists(modulesFolder) ? ModuleScanner.Scan(modulesFolder) : ModuleScanResult.Empty;
        var copies = Copies(modulesFolder);
        var held = HeldByAcf(workshopContent, installedOnly: false);
        var installed = HeldByAcf(workshopContent, installedOnly: true);
        var items = new List<WorkshopCopyItem>();
        var seenItems = new HashSet<string>(StringComparer.Ordinal);

        if (Directory.Exists(workshopContent))
        {
            foreach (var module in ModuleScanner.Scan(workshopContent).Modules.Where(m => !m.IsOfficial))
            {
                var itemId = ModulePageUrl.WorkshopIdOfFolder(module.FolderPath);

                if (itemId is null || !seenItems.Add(itemId))
                    continue;

                var found = copies.TryGetValue(itemId, out var copy);
                items.Add(new WorkshopCopyItem(
                    itemId, module.Id.Value, module.Name, module.FolderPath, found ? copy.Folder : null,
                    StateOf(module, found ? copy : null, local)));
            }
        }

        // A copy whose Workshop folder is not in the scan above: the folder is gone or no longer holds a manifest.
        foreach (var (itemId, copy) in copies.Where(c => !seenItems.Contains(c.Key)))
        {
            var source = Path.Combine(workshopContent, itemId);

            // Only Steam's own record may say the item is gone. Where it cannot be read (a library on a
            // drive that is not there, an unreadable file) nothing is claimed, so no warning is ever false.
            var state = held is null || Directory.Exists(source) ? WorkshopCopyState.Copied
                : installed is not null && installed.Contains(itemId) ? WorkshopCopyState.WorkshopFolderMissing
                : held.Contains(itemId) ? WorkshopCopyState.Copied
                : WorkshopCopyState.RemovedFromSubscriptions;

            items.Add(new WorkshopCopyItem(itemId, copy.Record.ModuleId, NameOf(copy.Folder, copy.Record), null, copy.Folder, state));
        }

        return items;
    }

    public static WorkshopCopyResult Copy(string gameInstallPath, string itemId)
    {
        var item = Survey(gameInstallPath).FirstOrDefault(i => i.ItemId == itemId);

        if (item is null)
            return new WorkshopCopyResult(
                new WorkshopCopyItem(itemId, itemId, itemId, null, null, WorkshopCopyState.NotCopied),
                WorkshopCopyOutcome.Skipped,
                Strings.Current.Format("Core.Modules.WorkshopCopy.NoSource", itemId));

        return Copy(gameInstallPath, item);
    }

    public static IReadOnlyList<WorkshopCopyResult> CopyAll(string gameInstallPath) =>
        [.. Survey(gameInstallPath).Where(i => i.NeedsCopy).Select(i => Copy(gameInstallPath, i))];

    public static WorkshopCopyResult Copy(string gameInstallPath, WorkshopCopyItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // The row the player pressed may be minutes old: the copy may have been edited or the module id
        // installed by hand since, so the decision is made on a fresh look, never on the row's state.
        var fresh = Survey(gameInstallPath).FirstOrDefault(i => i.ItemId == item.ItemId);

        if (fresh is null)
            return Skip(item, "Core.Modules.WorkshopCopy.NoSource", item.Name);

        item = fresh;
        string? staging = null;

        try
        {
            switch (item.State)
            {
                case WorkshopCopyState.Conflict:
                    return Skip(item, "Core.Modules.WorkshopCopy.Conflict", item.ModuleId);
                case WorkshopCopyState.WorkshopUpdatedCopyEdited:
                    return Skip(item, "Core.Modules.WorkshopCopy.Edited", item.Name);
                case not (WorkshopCopyState.NotCopied or WorkshopCopyState.WorkshopUpdated):
                    return new WorkshopCopyResult(item, WorkshopCopyOutcome.Skipped, string.Empty, item.CopyFolder);
            }

            if (item.SourceFolder is null || !Directory.Exists(item.SourceFolder))
                return Skip(item, "Core.Modules.WorkshopCopy.NoSource", item.Name);

            // Steam lists an item under WorkshopItemsInstalled only once its files are down; copying while
            // it is still downloading would capture a mix of versions.
            var workshopContent = ModuleScanner.GetWorkshopFolder(gameInstallPath);
            var installedIds = workshopContent is null ? null : HeldByAcf(workshopContent, installedOnly: true);

            if (installedIds is not null && !installedIds.Contains(item.ItemId))
                return Skip(item, "Core.Modules.WorkshopCopy.Changed", item.Name);

            var modulesFolder = ModuleScanner.GetModulesFolder(gameInstallPath);
            Directory.CreateDirectory(modulesFolder);

            var refresh = item.State == WorkshopCopyState.WorkshopUpdated && item.CopyFolder is not null;
            var destination = refresh ? item.CopyFolder! : FreeFolder(modulesFolder, item);

            if (destination is null)
                return Skip(item, "Core.Modules.WorkshopCopy.NameTaken", item.ModuleId);

            var stagingRoot = Path.Combine(Path.GetFullPath(gameInstallPath), StagingFolder);
            staging = Path.Combine(stagingRoot, Path.GetFileName(destination) + "." + item.ItemId);
            DirectoryMover.TryDeleteTree(staging);

            var before = Fingerprint(item.SourceFolder);
            DirectoryMover.Copy(item.SourceFolder, staging);
            ClearReadOnly(staging);

            if (!DirectoryContents.IsCoveredBy(item.SourceFolder, staging))
                return Fail(item, "Core.Modules.WorkshopCopy.Incomplete", item.Name);

            if (Fingerprint(item.SourceFolder) != before)
                return Fail(item, "Core.Modules.WorkshopCopy.Changed", item.Name);

            var record = new WorkshopCopyRecord(
                item.ItemId, item.ModuleId, item.SourceFolder, before, Fingerprint(staging), DateTime.UtcNow);
            File.WriteAllText(Path.Combine(staging, RecordFileName), JsonSerializer.Serialize(record));

            Install(staging, destination, refresh, stagingRoot);
            staging = null;

            return new WorkshopCopyResult(
                item,
                refresh ? WorkshopCopyOutcome.Refreshed : WorkshopCopyOutcome.Copied,
                string.Empty,
                destination);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail(item, "Core.Modules.WorkshopCopy.Failed", item.Name, ex.Message);
        }
        finally
        {
            if (staging is not null)
                DirectoryMover.TryDeleteTree(staging);

            RemoveEmpty(Path.Combine(Path.GetFullPath(gameInstallPath), StagingFolder));
        }
    }

    // A refresh that died between setting the old copy aside and moving the new one in leaves the player
    // with no copy in Modules and the old one in staging. It goes back before anything else runs.
    private static void RecoverPrevious(string gameInstallPath, string modulesFolder)
    {
        try
        {
            var root = Path.Combine(Path.GetFullPath(gameInstallPath), StagingFolder);

            if (!Directory.Exists(root))
                return;

            foreach (var aside in Directory.EnumerateDirectories(root, "*.previous"))
            {
                var name = Path.GetFileName(aside)[..^".previous".Length];
                var destination = Path.Combine(modulesFolder, name);

                if (!Directory.Exists(destination))
                {
                    Directory.CreateDirectory(modulesFolder);
                    Directory.Move(aside, destination);
                }
            }

            RemoveEmpty(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static WorkshopCopyResult Skip(WorkshopCopyItem item, string key, params object[] args) =>
        new(item, WorkshopCopyOutcome.Skipped, Strings.Current.Format(key, args));

    private static WorkshopCopyResult Fail(WorkshopCopyItem item, string key, params object[] args) =>
        new(item, WorkshopCopyOutcome.Failed, Strings.Current.Format(key, args));

    // A refresh sets the old copy aside first and puts it back if the new one cannot be moved in, so the
    // player never ends with neither.
    private static void Install(string staging, string destination, bool refresh, string stagingRoot)
    {
        if (!refresh)
        {
            Directory.Move(staging, destination);
            return;
        }

        var aside = Path.Combine(stagingRoot, Path.GetFileName(destination) + ".previous");
        DirectoryMover.TryDeleteTree(aside);
        Directory.Move(destination, aside);

        try
        {
            Directory.Move(staging, destination);
        }
        catch
        {
            if (!Directory.Exists(destination))
                Directory.Move(aside, destination);

            throw;
        }

        DirectoryMover.TryDeleteTree(aside);
    }

    // Modules\<ModuleId> when nothing is there; otherwise Modules\<ModuleId>_<item id>, and null when both
    // are taken. A module id with characters a folder name cannot hold is made safe the same way.
    private static string? FreeFolder(string modulesFolder, WorkshopCopyItem item)
    {
        var name = string.Concat(item.ModuleId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        foreach (var candidate in new[] { name, name + "_" + item.ItemId })
        {
            var path = Path.Combine(modulesFolder, candidate);

            if (!Directory.Exists(path) && !File.Exists(path))
                return path;
        }

        return null;
    }

    private static WorkshopCopyState StateOf(
        ModuleManifest workshopModule, (string Folder, WorkshopCopyRecord Record)? copy, ModuleScanResult local)
    {
        if (copy is not { } kept)
        {
            // A module with this id that BEM did not copy from this item (the player's own install, or a
            // copy of another item) means a second copy would be a duplicate id, which stops the game.
            var taken = local.Modules.Any(m => m.Id == workshopModule.Id)
                || local.Unreadable.Any(u => string.Equals(u.DeclaredId ?? u.FolderName, workshopModule.Id.Value, StringComparison.OrdinalIgnoreCase));

            return taken ? WorkshopCopyState.Conflict : WorkshopCopyState.NotCopied;
        }

        string current;

        try
        {
            current = Fingerprint(workshopModule.FolderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return WorkshopCopyState.Copied;
        }

        if (current == kept.Record.SourceFingerprint)
            return WorkshopCopyState.Copied;

        // An update that renamed the module id must not land on an id another folder already holds.
        if (!string.Equals(kept.Record.ModuleId, workshopModule.Id.Value, StringComparison.Ordinal)
            && (local.Modules.Any(m => m.Id == workshopModule.Id && !SameFolder(m.FolderPath, kept.Folder))
                || local.Unreadable.Any(u => string.Equals(u.DeclaredId ?? u.FolderName, workshopModule.Id.Value, StringComparison.OrdinalIgnoreCase))))
            return WorkshopCopyState.Conflict;

        try
        {
            return Fingerprint(kept.Folder) == kept.Record.CopyFingerprint
                ? WorkshopCopyState.WorkshopUpdated
                : WorkshopCopyState.WorkshopUpdatedCopyEdited;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return WorkshopCopyState.WorkshopUpdatedCopyEdited;
        }
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    private static string NameOf(string folder, WorkshopCopyRecord record)
    {
        try
        {
            return ModuleScanner.Scan(Path.GetDirectoryName(folder)!).Modules
                .FirstOrDefault(m => string.Equals(m.FolderPath, folder, StringComparison.OrdinalIgnoreCase))?.Name ?? record.ModuleId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return record.ModuleId;
        }
    }

    // Steam's own record, read beside the content folder. Null whenever it cannot be read, and also when
    // the content folder's library is not reachable, because then every item would look removed.
    private static IReadOnlySet<string>? HeldByAcf(string workshopContent, bool installedOnly)
    {
        var steamapps = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(workshopContent)));

        if (steamapps is null || !Directory.Exists(steamapps))
            return null;

        var acf = WorkshopManifest.PathFor(steamapps, Path.GetFileName(workshopContent));
        return installedOnly ? WorkshopManifest.InstalledItemIds(acf) : WorkshopManifest.HeldItemIds(acf);
    }

    private static Dictionary<string, (string Folder, WorkshopCopyRecord Record)> Copies(string modulesFolder)
    {
        var copies = new Dictionary<string, (string, WorkshopCopyRecord)>(StringComparer.Ordinal);

        if (!Directory.Exists(modulesFolder))
            return copies;

        try
        {
            foreach (var folder in Directory.EnumerateDirectories(modulesFolder))
            {
                if (ReadRecord(folder) is { } record)
                    copies.TryAdd(record.ItemId, (folder, record));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return copies;
    }

    private static WorkshopCopyRecord? ReadRecord(string folder)
    {
        try
        {
            var path = Path.Combine(folder, RecordFileName);

            return File.Exists(path) && JsonSerializer.Deserialize<WorkshopCopyRecord>(File.ReadAllText(path)) is { ItemId.Length: > 0 } record
                ? record
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Path, size and write time of every file except the record, in path order. A Steam update changes at
    // least one of them, and the copy's own is recorded so an edit made to the copy afterwards shows.
    private static string Fingerprint(string folder)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };

        foreach (var path in Directory.EnumerateFiles(folder, "*", options)
                     .Select(f => (Full: f, Relative: Path.GetRelativePath(folder, f).Replace('\\', '/')))
                     .Where(f => f.Relative != RecordFileName)
                     .OrderBy(f => f.Relative, StringComparer.Ordinal))
        {
            var info = new FileInfo(path.Full);
            hash.AppendData(Encoding.UTF8.GetBytes($"{path.Relative}|{info.Length}|{info.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerSecond}\n"));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // A source that is read-only stays read-only, but the copy is the player's to manage and delete.
    private static void ClearReadOnly(string folder)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(entry);

            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static void RemoveEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
