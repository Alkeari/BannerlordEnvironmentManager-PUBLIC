using BannerlordEnvironmentManager.Core.Io;

namespace BannerlordEnvironmentManager.Core.GameSettings;

public enum GameSettingsOutcome
{
    // The file on disk was rewritten, restored or folded into the shared file.
    Applied,

    // The instance had no such file, so BEM wrote one holding the shared values: see GameSettingsSeed
    // for what that rests on. Reported apart from Applied because nothing of the instance's own was
    // merged into it and nothing of the instance's own is put back when sharing is turned off.
    Seeded,

    // The file already reads exactly as the merge says it should, so nothing was written.
    AlreadyMatching,

    // The instance has no such file and BEM cannot compose one: either the shared set has nothing for
    // that kind yet, or it is BannerlordGameKeys.xml, which GameSettingsFiles.FormatOf declines.
    NotPresent,

    // Sharing has never frozen this instance's file, so there is nothing of its own to fold back.
    NotShared,

    // The capture after the first run on a file BEM seeded. What that run wrote became the instance's
    // base copy and nothing of it reached the shared file.
    FirstRun,

    // The file could not be read or written; the file on disk is untouched.
    Failed
}

public sealed record GameSettingsFileResult(
    GameSettingsFileKind Kind,
    GameSettingsOutcome Outcome,
    int Changed = 0,
    int Added = 0,
    string? Error = null);

public sealed record GameSettingsSyncResult(IReadOnlyList<GameSettingsFileResult> Files)
{
    public int Changed => Files.Sum(file => file.Changed);

    public int Added => Files.Sum(file => file.Added);

    public bool Failed => Files.Any(file => file.Outcome == GameSettingsOutcome.Failed);

    public bool DidSomething =>
        Files.Any(file => file.Outcome is GameSettingsOutcome.Applied or GameSettingsOutcome.Seeded);

    public int Seeded => Files.Count(file => file.Outcome == GameSettingsOutcome.Seeded);

    public IReadOnlyList<GameSettingsFileResult> Failures =>
        [.. Files.Where(file => file.Outcome == GameSettingsOutcome.Failed)];
}

public sealed record GameSettingsInstanceResult(string InstanceFolder, GameSettingsSyncResult Result);

// Turning sharing off puts every instance back at once rather than at each one's next launch, because
// the off state is "each instance uses its own settings file" in the present tense. One instance that
// cannot be written does not stop the rest.
public sealed record GameSettingsRestoreReport(IReadOnlyList<GameSettingsInstanceResult> Instances)
{
    public int Restored => Instances.Count(instance => instance.Result.DidSomething);

    public bool Failed => Instances.Any(instance => instance.Result.Failed);

    public IReadOnlyList<GameSettingsInstanceResult> Failures =>
        [.. Instances.Where(instance => instance.Result.Failed)];
}

// Sharing the base game's settings across instances, off unless the player turns it on.
//
// Apply runs before a launch and Capture after one. Apply projects the shared set onto the file the
// instance actually has, never onto the frozen copy, and the frozen copy's one remaining job is
// Restore. Projection is an intersection, so live as the base changes exactly one thing: a key the
// live file has and the shared set does not keeps its own value instead of being taken back to
// whatever it held on the day sharing first touched it. That key is the mod hotkey category bound
// since the freeze and the setting a game update introduced, and projecting from the frozen copy
// deleted both, every launch, for good. The keys GameSettingsExclusions names need no carrying
// forward any more: the projection skips them and the live file already holds the instance's own
// value for each.
//
// An instance with no such file at all is the one case with nothing to start from, and it is the
// common one: a version downloaded and launched for the first time is exactly when a player expects
// their settings to follow them. GameSettingsSeed writes the file instead, planted rather than
// frozen, and the capture after that first run replaces the seed with what the build itself wrote.
//
// That first run is the one whose output is never taken as the player's. A build meeting a settings
// file it did not write can rewrite a whole family of graphics settings on its own, and folding that
// in put one build's low defaults onto every other instance at its next launch. Every later run is
// taken at its word: what the file says differently from the shared set is the player's edit. A build
// too old to represent a value writes its own back and that does reach the shared file, because no
// rule reading one settings file tells it apart from an edit, and the rule that tried read the
// player's own repeated edits as the game's and kept them out for good.
public sealed class GameSettingsSync(SharedGameSettingsStore store)
{
    // What earlier versions wrote beside the base copies to record each launch's application. Nothing
    // reads it now, and one left behind would keep the folder from being removed when sharing goes off.
    private const string RetiredAppliedRecord = "applied-game-settings.json";

    private readonly SharedGameSettingsStore store = store ?? throw new ArgumentNullException(nameof(store));

    public static GameSettingsSync Beside(string instanceSettingsPath) =>
        new(SharedGameSettingsStore.Beside(instanceSettingsPath));

    public GameSettingsSyncResult Apply(GameSettingsTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var shared = this.store.Read();

        return new GameSettingsSyncResult([.. GameSettingsFiles.All.Select(kind => Apply(target, kind, shared.For(kind)))]);
    }

    public GameSettingsSyncResult Capture(GameSettingsTarget target) => Capture(target, requireFrozen: true);

    // The first enable has nothing to project yet, so the instance the user is on supplies the whole
    // starting set. It reads the live files rather than a frozen copy, because no instance has been
    // frozen at that point and the live files are still entirely the player's own.
    public GameSettingsSyncResult Seed(GameSettingsTarget target) => Capture(target, requireFrozen: false);

    private GameSettingsSyncResult Capture(GameSettingsTarget target, bool requireFrozen)
    {
        ArgumentNullException.ThrowIfNull(target);

        var shared = this.store.Read();
        var results = new List<GameSettingsFileResult>();
        var changed = false;

        foreach (var kind in GameSettingsFiles.All)
        {
            var (result, captured) = Capture(target, kind, shared.For(kind), requireFrozen);

            results.Add(result);

            if (captured is null)
                continue;

            shared = shared.With(kind, captured.Shared);
            changed |= result.Outcome == GameSettingsOutcome.Applied;
        }

        if (changed)
            this.store.Write(shared);

        return new GameSettingsSyncResult(results);
    }

    public GameSettingsSyncResult Restore(GameSettingsTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var results = new List<GameSettingsFileResult>();

        foreach (var kind in GameSettingsFiles.All)
        {
            try
            {
                results.Add(new GameSettingsFileResult(
                    kind,
                    OwnGameSettings.Restore(target, kind)
                        ? GameSettingsOutcome.Applied
                        : GameSettingsOutcome.NotShared));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new GameSettingsFileResult(kind, GameSettingsOutcome.Failed, Error: ex.Message));
            }
        }

        try
        {
            File.Delete(Path.Combine(OwnGameSettings.FolderFor(target.InstanceFolder), RetiredAppliedRecord));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale record left behind costs an empty folder that stays; the restore itself succeeded.
        }

        OwnGameSettings.RemoveFolderIfEmpty(target.InstanceFolder);

        return new GameSettingsSyncResult(results);
    }

    // Every instance goes back at once when the toggle goes off. An instance that cannot be written
    // is reported and the rest still get their own files back, because stopping at the first failure
    // would leave the remaining instances sharing settings the toggle says they are not.
    public GameSettingsRestoreReport RestoreAll(IEnumerable<GameSettingsTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        return new GameSettingsRestoreReport(
            [.. targets.Select(target => new GameSettingsInstanceResult(target.InstanceFolder, Restore(target)))]);
    }

    public static bool IsShared(string instanceFolder) =>
        GameSettingsFiles.All.Any(kind => OwnGameSettings.IsFrozen(instanceFolder, kind));

    private static GameSettingsFileResult Apply(
        GameSettingsTarget target,
        GameSettingsFileKind kind,
        IReadOnlyDictionary<string, string> shared)
    {
        var live = OwnGameSettings.LivePath(target, kind);

        try
        {
            // The instance has never had this file. Doing nothing here is what left a freshly
            // downloaded version on the game's defaults, which is the launch a player most wants
            // their settings to survive, so the shared set is written into a new file instead.
            if (!File.Exists(live))
                return Seed(target, kind, shared);

            // Before the first byte is written to the file it protects, so that a write that fails
            // still leaves the player a copy of their own settings they can put back without BEM.
            OwnGameSettings.Freeze(target, kind);

            var document = GameSettingsFiles.Read(kind, live);
            var projection = GameSettingsMerge.Project(document, kind, shared);

            var bytes = document.ToBytes();

            if (bytes.AsSpan().SequenceEqual(File.ReadAllBytes(live)))
                return new GameSettingsFileResult(kind, GameSettingsOutcome.AlreadyMatching, projection.Changed);

            AtomicXmlFile.Save(bytes, live, writeBackup: false);

            return new GameSettingsFileResult(kind, GameSettingsOutcome.Applied, projection.Changed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or System.Xml.XmlException)
        {
            return new GameSettingsFileResult(kind, GameSettingsOutcome.Failed, Error: ex.Message);
        }
    }

    private static GameSettingsFileResult Seed(
        GameSettingsTarget target,
        GameSettingsFileKind kind,
        IReadOnlyDictionary<string, string> shared)
    {
        if (GameSettingsSeed.Build(kind, shared) is not { } seed)
            return new GameSettingsFileResult(kind, GameSettingsOutcome.NotPresent);

        OwnGameSettings.Plant(target, kind, seed.Bytes);

        return new GameSettingsFileResult(kind, GameSettingsOutcome.Seeded, Added: seed.Values.Count);
    }

    private static (GameSettingsFileResult Result, GameSettingsCapture? Captured) Capture(
        GameSettingsTarget target,
        GameSettingsFileKind kind,
        IReadOnlyDictionary<string, string> shared,
        bool requireFrozen)
    {
        try
        {
            if (requireFrozen && !OwnGameSettings.IsFrozen(target.InstanceFolder, kind))
                return (new GameSettingsFileResult(kind, GameSettingsOutcome.NotShared), null);

            var live = OwnGameSettings.LivePath(target, kind);

            if (!File.Exists(live))
                return (new GameSettingsFileResult(kind, GameSettingsOutcome.NotPresent), null);

            // The run that has just ended is the first thing to tell BEM what this build's settings
            // file actually looks like, so the base copy that is still BEM's own seed is replaced by
            // it: every later projection then starts from the build's real key set rather than from
            // the shape of whichever instance the shared set was captured off. Its values stay here.
            // A launch that never got as far as the game writing the file leaves the seed in place,
            // and the next run that does write it is still the first.
            if (SeededGameSettings.StateOf(target.InstanceFolder, kind) == GameSettingsSeedState.Seed)
            {
                OwnGameSettings.Grow(target, kind);

                return (new GameSettingsFileResult(kind, GameSettingsOutcome.FirstRun), null);
            }

            var captured = GameSettingsMerge.Capture(shared, GameSettingsFiles.Read(kind, live), kind);
            var outcome = captured.Added + captured.Updated > 0
                ? GameSettingsOutcome.Applied
                : GameSettingsOutcome.AlreadyMatching;

            return (new GameSettingsFileResult(kind, outcome, captured.Updated, captured.Added), captured);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or System.Xml.XmlException)
        {
            return (new GameSettingsFileResult(kind, GameSettingsOutcome.Failed, Error: ex.Message), null);
        }
    }
}
