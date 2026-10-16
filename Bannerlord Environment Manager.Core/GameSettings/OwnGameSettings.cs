using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Io;

namespace BannerlordEnvironmentManager.Core.GameSettings;

// The instance's own settings, frozen the first time sharing touches that file and never written
// again while sharing is on. It is what goes back when sharing is turned off, which is what makes the
// toggle honestly reversible: the file the player had before is still on disk, byte for byte, rather
// than reconstructed from what BEM thinks it was. It is deliberately not the merge base. A projection
// starts from the file the instance has now, because a base fixed at one moment carries no key the
// instance gained after it, and writing that base back would delete every such key: the hotkey
// category a mod registered, the setting a game update introduced.
//
// Frozen per file rather than per instance. An instance BEM has downloaded but never launched has no
// Configs folder at all, so an instance-wide marker would record it as frozen while there was
// nothing to freeze, and the files the game wrote on its first launch would then never have a base.
// A file with no frozen copy has by definition never been projected onto, so whatever it holds now
// is genuinely its own and is safe to freeze at that point.
//
// That same instance is the one sharing used to do nothing for at all, so a second kind of base copy
// lives here too: a file BEM wrote because there was none, planted rather than frozen and marked as
// BEM's by SeededGameSettings. It is merged from exactly like a frozen file, and it is the one thing
// a restore takes away instead of putting back.
public static class OwnGameSettings
{
    public static string FolderFor(string instanceFolder) =>
        InstanceLayout.OwnGameSettingsFolder(instanceFolder);

    public static string FrozenPath(string instanceFolder, GameSettingsFileKind kind) =>
        Path.Combine(FolderFor(instanceFolder), GameSettingsFiles.NameOf(kind));

    // The live path comes from the target's own data root, never from the instance folder: see
    // GameSettingsTarget for why the two differ for the resting install.
    public static string LivePath(GameSettingsTarget target, GameSettingsFileKind kind) =>
        Path.Combine(target.Configs, GameSettingsFiles.NameOf(kind));

    // "There is a base copy to merge from", which a seeded file also has. What separates the two is
    // SeededGameSettings, and only the restore has to care: a seed is merged from exactly like a
    // frozen file and is put back like nothing at all.
    public static bool IsFrozen(string instanceFolder, GameSettingsFileKind kind) =>
        File.Exists(FrozenPath(instanceFolder, kind));

    // True only when this call is what froze it. An already frozen file is left exactly as it is,
    // because overwriting it would make the merge base whatever the last projection produced and lose
    // the only copy of what the player actually had.
    public static bool Freeze(GameSettingsTarget target, GameSettingsFileKind kind)
    {
        var frozen = FrozenPath(target.InstanceFolder, kind);

        if (File.Exists(frozen))
            return false;

        var live = LivePath(target, kind);

        if (!File.Exists(live))
            return false;

        Directory.CreateDirectory(FolderFor(target.InstanceFolder));
        File.Copy(live, frozen, overwrite: false);

        return true;
    }

    // Writes a file the instance did not have, and records that BEM is the one that wrote it.
    //
    // The marker and the base copy go down before the live file, the reverse of the freeze order and
    // for the same reason: a live file with no marker beside it would be read as the player's own at
    // the next apply, and frozen as if it were. If the live write then fails, the instance is left
    // with no file at all and a base copy waiting, which the next apply seeds over.
    public static void Plant(GameSettingsTarget target, GameSettingsFileKind kind, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(bytes);

        Directory.CreateDirectory(FolderFor(target.InstanceFolder));
        SeededGameSettings.Mark(target.InstanceFolder, kind, GameSettingsSeedState.Seed);
        File.WriteAllBytes(FrozenPath(target.InstanceFolder, kind), bytes);
        AtomicXmlFile.Save(bytes, LivePath(target, kind), writeBackup: false);
    }

    // The seed was BEM's composition of a file the instance did not have, and the run that has just
    // ended produced the real one: the build's own key set, in the build's own order. The base copy
    // is replaced by it so that what a restore would delete is the file the instance is actually on
    // rather than BEM's first guess at it.
    //
    // The marker stays. The file is still not the player's own, and turning sharing off must still
    // take it away rather than hand them a file they never had.
    public static bool Grow(GameSettingsTarget target, GameSettingsFileKind kind)
    {
        ArgumentNullException.ThrowIfNull(target);

        var live = LivePath(target, kind);
        var frozen = FrozenPath(target.InstanceFolder, kind);

        if (SeededGameSettings.StateOf(target.InstanceFolder, kind) != GameSettingsSeedState.Seed
            || !File.Exists(live))
            return false;

        // A file still identical to the seed is one the game has not written, which is a launch that
        // did not get as far as starting. Growing on that would spend the one transition there is and
        // leave the seed's own shape as the base for good, so the state stays where it is and the
        // next run that does write something takes it.
        if (File.Exists(frozen) && File.ReadAllBytes(live).AsSpan().SequenceEqual(File.ReadAllBytes(frozen)))
            return false;

        Directory.CreateDirectory(FolderFor(target.InstanceFolder));
        File.Copy(live, frozen, overwrite: true);
        SeededGameSettings.Mark(target.InstanceFolder, kind, GameSettingsSeedState.Grown);

        return true;
    }

    // The frozen copy is consumed by the restore, so a later enable freezes the file the player has
    // now rather than reviving a snapshot from a sharing session they already ended.
    //
    // A base copy BEM wrote is taken away instead, in either of its states: GameSettingsSeedState
    // carries why.
    public static bool Restore(GameSettingsTarget target, GameSettingsFileKind kind)
    {
        var frozen = FrozenPath(target.InstanceFolder, kind);

        if (SeededGameSettings.StateOf(target.InstanceFolder, kind) != GameSettingsSeedState.None)
            return Discard(target, kind, frozen);

        if (!File.Exists(frozen))
            return false;

        var live = LivePath(target, kind);
        Directory.CreateDirectory(Path.GetDirectoryName(live)!);
        File.Copy(frozen, live, overwrite: true);
        File.Delete(frozen);

        return true;
    }

    private static bool Discard(GameSettingsTarget target, GameSettingsFileKind kind, string frozen)
    {
        var live = LivePath(target, kind);

        if (File.Exists(live))
            File.Delete(live);

        if (File.Exists(frozen))
            File.Delete(frozen);

        SeededGameSettings.Clear(target.InstanceFolder, kind);

        return true;
    }

    public static void RemoveFolderIfEmpty(string instanceFolder)
    {
        var folder = FolderFor(instanceFolder);

        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty folder left behind is untidy and nothing more; the restore itself succeeded.
        }
    }
}
