using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules.KnownIssues;

namespace BannerlordEnvironmentManager.Core.Install;

public enum UndoKind
{
    ModuleFile,
    BinFile,
    ModuleFolder,
    BundledCopies,
    ShaderFolder,
    PlatformFolder,
    AdoptedPlatformFolder
}

// One thing BEM did that can be put back, whichever store holds the means to put it back. Source is the
// store's own record, so the caller restores it through that store's existing restore and nothing here
// has to know how.
public sealed record UndoEntry(UndoKind Kind, string Key, string Title, string Detail, DateTime WhenUtc, object Source)
{
    // The time it happened, then where it lives, on the second line of a row. A file whose time was
    // never recorded shows the place alone.
    public string Subtitle => WhenUtc == DateTime.MinValue
        ? Detail
        : $"{WhenUtc.ToLocalTime():yyyy-MM-dd HH:mm} - {Detail}";

    // A list item with no automation name of its own is announced by its ToString.
    public override string ToString() => Title;
}

// The six places BEM keeps something to put back, merged into the one list the Library page shows,
// newest first. Pure: the caller reads the stores, this orders what it found.
public static class UndoList
{
    public static IReadOnlyList<UndoEntry> Build(
        IEnumerable<ReplacedFile>? moduleFiles,
        IEnumerable<BinBackup>? binBackups,
        IEnumerable<QuarantinedItem>? quarantined,
        IEnumerable<SetAsideRecord>? setAside,
        IEnumerable<AdoptedPlatformRecord>? adopted = null)
    {
        var entries = new List<UndoEntry>();

        foreach (var file in moduleFiles ?? [])
        {
            entries.Add(new UndoEntry(
                UndoKind.ModuleFile, file.TargetPath,
                Strings.Current.Format("Core.Install.Undo.ModuleFile", file.RelativePath),
                file.TargetPath, AsUtc(file.ReplacedUtc), file));
        }

        foreach (var backup in binBackups ?? [])
        {
            entries.Add(new UndoEntry(
                UndoKind.BinFile, backup.BackupPath,
                Strings.Current.Format("Core.Install.Undo.BinFile", backup.GameRelativePath),
                backup.DestinationPath, AsUtc(backup.CreatedUtc), backup));
        }

        foreach (var item in quarantined ?? [])
        {
            if (KindOf(item) is not { } kind)
                continue;

            var key = kind switch
            {
                UndoKind.ModuleFolder => "Core.Install.Undo.ModuleFolder",
                UndoKind.ShaderFolder => "Core.Install.Undo.ShaderFolder",
                _ => "Core.Install.Undo.PlatformFolder"
            };

            entries.Add(new UndoEntry(
                kind, item.Id,
                Strings.Current.Format(key, Path.GetFileName(item.OriginPath.TrimEnd('\\', '/')), item.RelativePath),
                item.OriginPath, AsUtc(item.QuarantinedUtc.UtcDateTime), item));
        }

        foreach (var record in setAside ?? [])
        {
            entries.Add(new UndoEntry(
                UndoKind.BundledCopies, record.BackupFolder,
                Strings.Current.Format("Core.Install.Undo.BundledCopies", record.Bundler),
                record.BundlerFolder, AsUtc(record.CreatedUtc), record));
        }

        foreach (var record in adopted ?? [])
        {
            if (record.Undone)
                continue;

            entries.Add(new UndoEntry(
                UndoKind.AdoptedPlatformFolder, record.Id,
                Strings.Current.Format("Core.Install.Undo.AdoptedPlatformFolder", record.ModuleFolderName, record.TargetPlatformFolder),
                record.TargetPath, AsUtc(record.AdoptedUtc.UtcDateTime), record));
        }

        // Stable, so two entries stamped in the same instant keep the order the stores gave them.
        return [.. entries.Select((entry, index) => (entry, index))
            .OrderByDescending(pair => pair.entry.WhenUtc)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.entry)];
    }

    public static UndoKind? KindOf(QuarantinedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Group == ReplacedModuleQuarantine.Group)
            return UndoKind.ModuleFolder;

        if (item.Reason == ShaderQuarantine.Reason)
            return UndoKind.ShaderFolder;

        return item.Reason is PlatformBinaries.Reason or PlatformBinaries.AdoptionReason
            ? UndoKind.PlatformFolder
            : null;
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Utc => value,
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
