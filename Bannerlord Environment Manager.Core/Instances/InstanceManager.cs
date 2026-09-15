using System.Text.Json;
using System.Text.Json.Serialization;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Instances;

// The one type the app calls instead of assembling InstanceRegistry, InstanceAdoption,
// ActivationSession and StartupRepair by hand. Everything here composes those types; none of their
// behavior is reimplemented.
//
// stateAreas is where BEM's own per-version state sits, which is the two real folders on a machine and
// a temporary pair under test. It is a constructor parameter only so that a resting change can be
// driven end to end without moving the machine's real state around.
public sealed class InstanceManager(
    CanonicalPathSet paths, InstanceSettingsStore settings, IReadOnlyList<StateArea>? stateAreas = null)
{
    // The captured crash artifacts are the second area. They key themselves on InstanceStateFolder the
    // same way everything else does, but their folder sits outside BEM's own because a folder a crash
    // scan walks may never sit under one holding the Nexus key. Root and entry name are read back off
    // the store's own resting path rather than restated, so renaming either one there cannot leave this
    // moving a folder that no longer exists.
    private static readonly StateArea CapturedArtifacts = new(
        Path.GetDirectoryName(CrashArtifactPaths.GetDefaultRoot())!,
        [Path.GetFileName(CrashArtifactPaths.GetDefaultRoot())]);

    private IReadOnlyList<StateArea> StateAreas => stateAreas
        ?? [new StateArea(InstanceStateFolder.MachineRoot(), InstanceStateFolder.StateEntryNames), CapturedArtifacts];

    // Where the activation and teardown of a launch are written down. Core owns no logger, so the app
    // hands one in; nothing here depends on it being set. Its absence is why a launch that never
    // finished could not be diagnosed from the log at all.
    public Action<string>? Log { get; set; }

    // How long a caller waits on the junction teardown before being handed a result and let go. Null
    // takes ActivationSession's own default.
    public TimeSpan? TeardownBudget { get; set; }

    // True while this process is inside an activation for these canonical paths, a teardown running
    // past its budget included. The lock refuses a second one, so a control offered while this is true
    // offers a press that cannot be granted; the phase of one page's own launch cannot answer it,
    // because a dry run or a bisection started elsewhere holds the same lock.
    public bool ActivationHeldHere => ActivationLock.IsHeldByThisProcess(paths);

    public string? RestingInstanceId
    {
        get
        {
            var id = settings.Read().RestingInstanceId;
            return string.IsNullOrEmpty(id) ? null : id;
        }
    }

    // The instance the next launch runs. Falls back to the resting instance because nothing writes
    // ActiveInstanceId yet: today's app has no version switcher to pick one, so active and resting
    // are the same instance until that lands. Reading it through here rather than RestingInstanceId
    // directly means the app already asks the right question and the switcher only has to start
    // answering it differently.
    public string? ActiveInstanceId
    {
        get
        {
            var current = settings.Read();
            var id = string.IsNullOrEmpty(current.ActiveInstanceId) ? current.RestingInstanceId : current.ActiveInstanceId;

            if (string.IsNullOrEmpty(id))
                return null;

            // An instance can be removed while its id is still written down here, and a launch under
            // an id no instance carries fails with a raw GUID the user cannot act on. The resting
            // instance is the one place there is always something to fall back to.
            if (Registry().Find(id) is not null)
                return id;

            return string.IsNullOrEmpty(current.RestingInstanceId) ? null : current.RestingInstanceId;
        }
    }

    public IReadOnlyList<InstalledInstance> List() => Registry().ReadInstalled();

    public InstalledInstance? Active()
    {
        if (ActiveInstanceId is not { } id)
            return null;

        var registry = Registry();
        var record = registry.Find(id);

        if (record is null)
            return null;

        var folder = registry.FolderOf(record);

        return folder is null ? null : new InstalledInstance(folder, record);
    }

    // Every registered instance whose game files sit at the top of its folder instead of inside
    // Game\, moved down one level and its record corrected. Returns how many were put right. Cheap
    // when there is nothing to do: one directory check per instance.
    public int RepairInstanceLayouts()
    {
        var registry = Registry();
        var repaired = 0;

        foreach (var instance in registry.ReadInstalled())
        {
            if (!InstanceLayoutRepair.Repair(instance.Folder))
                continue;

            var version = InstanceLayoutRepair.VersionOf(instance.Folder).ToString();

            registry.Write(instance.Record with
            {
                GameFolder = InstanceLayout.GameFolder(instance.Folder),
                GameFolderIsReferenced = false,
                RecordedGameVersion = version.Length > 0 ? version : instance.Record.RecordedGameVersion
            });

            repaired++;
        }

        return repaired;
    }

    // A folder in the games root that holds a game but no instance.json: a download that finished
    // after the app was closed, a folder the user copied in, or one an older build left behind under
    // a numbered name. It is put into the shape every instance has, given the name its version earns,
    // and written down. Returns how many were taken in.
    //
    // A folder written to in the last two minutes is left alone: that is a download still running,
    // and moving files out from under it would cost the whole download.
    // candidateFor answers how many bytes that version is when it is all there, and which branch and
    // build id it was offered under, from what the catalog the caller already fetched says about that
    // version. A folder short of the expected byte count is a download that stopped, and adopting it
    // would write down a version that is missing files and let it be launched; a version the caller
    // cannot match to an offered branch (nothing fetched, no connection) is left alone for the same
    // reason, and its branch and build id are left unrecorded rather than guessed.
    public int AdoptUnregisteredFolders(Func<ModuleVersion, AdoptCandidate?> candidateFor)
    {
        ArgumentNullException.ThrowIfNull(candidateFor);

        var registry = Registry();
        var gamesRoot = registry.GamesRoot;

        if (!Directory.Exists(gamesRoot))
            return 0;

        var adopted = 0;

        foreach (var folder in Directory.EnumerateDirectories(gamesRoot))
        {
            if (Path.GetFileName(folder).StartsWith('.') || File.Exists(InstanceLayout.MetadataPath(folder)))
                continue;

            if (WrittenRecently(folder) || !InstanceLayoutRepair.HoldsGame(folder))
                continue;

            InstanceLayoutRepair.Repair(folder);

            var found = InstanceLayoutRepair.VersionOf(folder);
            var version = found.ToString();

            if (version.Length == 0)
                continue;

            var candidate = candidateFor(found);

            if (candidate is null)
                continue;

            var onDisk = UninstallReport.SizeOf(folder);

            if (!InstanceCompleteness.Holds(onDisk, candidate.ExpectedSizeBytes))
                continue;

            var record = new InstanceRecord(
                registry.MintId(version, candidate.Branch, GameDlcSet.Empty),
                version,
                candidate.Branch,
                candidate.BuildId,
                InstanceLayout.GameFolder(folder),
                false,
                version,
                version,
                DateTimeOffset.UtcNow,
                null);

            var home = InstanceLayoutRepair.RenameToVersion(folder, gamesRoot, version) ?? folder;

            WriteRecordTo(home, record with { GameFolder = InstanceLayout.GameFolder(home) });
            adopted++;
        }

        return adopted;
    }

    // What AdoptUnregisteredFolders needs to know about a version before it will take an unregistered
    // folder in: the byte count Steam publishes for it, and the branch and build id the catalog the
    // caller already fetched found it under. Carrying all three together means a folder is never
    // sized against one branch while its record is written down against another.
    public sealed record AdoptCandidate(long ExpectedSizeBytes, string Branch, string BuildId);

    // Anything at all touched inside the last two minutes counts, including the tool's own manifest
    // cache: a 60 GB download writes constantly, and a quiet folder is a finished one.
    private static bool WrittenRecently(string folder)
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(2);
            var walk = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

            return Directory.EnumerateFileSystemEntries(folder, "*", walk)
                .Any(entry => File.GetLastWriteTimeUtc(entry) > cutoff);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public RepairReport RepairOnStartup()
    {
        var restingFolder = RequireRestingFolder();

        return new StartupRepair(paths, restingFolder, Log).Run();
    }

    // Every instance folder brought into line with what the instance is: the resting role if it holds
    // it, and otherwise the purpose, the chosen name and the version with its variant, as
    // InstanceFolderName composes them.
    //
    // It takes the same lock a launch takes, for the same reason StartupRepair does. A launch holds
    // the canonical paths as junctions into an instance's own UserData, and renaming the folder under
    // a live junction would point the running game at a path that no longer exists. Waiting for the
    // launch is right here: nothing about a folder name is urgent, and a refused lock is reported
    // rather than thrown, so a startup that cannot rename anything still starts.
    public InstanceFolderAlignmentReport AlignFolderNames(string? onlyInstanceId = null)
    {
        ActivationLock activation;

        try
        {
            activation = ActivationLock.Acquire(paths);
        }
        catch (IOException ex)
        {
            return new InstanceFolderAlignmentReport([], [], ex.Message);
        }

        using (activation)
        {
            return InstanceFolderAlignment.Run(
                Registry().ReadInstalled(),
                [.. StateAreas.Select(area => area.Root)],
                onlyInstanceId,
                RestingInstanceId);
        }
    }

    // The current on-disk install becomes the resting instance. Adoption backs up what is already at
    // the canonical paths and leaves them exactly where they were, per the resting-instance-at-rest
    // invariant; nothing is moved into a store here.
    public InstalledInstance AdoptCurrentInstall(string gameFolder, string displayName, string branch, string buildId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);

        if (RestingInstanceId is not null)
            throw new InvalidOperationException(
                "A resting instance is already set, so adopting the current install was refused. "
                + "Adoption only runs once, to turn the pre-existing install into the first instance.");

        var registry = Registry();
        var record = NewRecord(
            registry, gameFolder, displayName, branch, buildId, gameFolderIsReferenced: true, GameDlcSet.Empty);

        registry.Write(record);
        var folder = registry.FolderOf(record) ?? registry.FolderFor(record);

        new InstanceAdoption(paths).Adopt(folder);

        WriteSettings(current => current with { RestingInstanceId = record.Id });

        return new InstalledInstance(folder, record);
    }

    // A folder that already holds a game, written down as an instance. Used by the downloader once a
    // new version has finished extracting; its store starts empty, same as any other resting-turned-
    // parked instance. The metadata goes into the folder the caller names, not one InstanceRegistry
    // would derive from the display name, because that folder already exists and already holds the
    // game: nothing else is free to pick where it lives.
    //
    // variant is the variant the download was for, and it shapes the Id alone, exactly as it already
    // shapes the folder the caller picked with FolderForDownload before a byte was fetched. It is
    // not what the record says it carries: Dlc stays empty here whatever the variant, and SetDlc
    // writes only what actually landed. Without it a v1.4.8 + War Sails download and a plain v1.4.8
    // mint one Id and the second shadows the first.
    //
    // chosenName is the name a second copy of a version already here was given before its download
    // started. It is written with the record rather than renamed in afterwards so the copy is never
    // listed, even for the moment before a second write, under the same name as the copy it was asked
    // for beside. A name too long to store is dropped rather than refused: the download has already
    // run by the time this is reached, and losing it over a name would be the worse answer.
    //
    // purpose is what the download was asked for, declared in the same dialog as the name and for the
    // same reason: the folder states the purpose, so an instance that has to be declared afterwards
    // is born under a name that is already wrong and waits for the alignment pass to correct it.
    // Unspecified is the default here as it is on the record, so a caller that does not ask declares
    // nothing. SetPurpose is not on this path and its refusal cannot be reached from it: a download
    // never registers the resting instance, and this writes a new record rather than changing one.
    public InstalledInstance Register(
        string instanceFolder, string displayName, string branch, string buildId, GameDlcSet variant = default,
        string? chosenName = null, InstancePurpose purpose = InstancePurpose.Unspecified)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceFolder);

        var record = NewRecord(
            Registry(), InstanceLayout.GameFolder(instanceFolder), displayName, branch, buildId,
            gameFolderIsReferenced: false, variant, chosenName, purpose);

        // A brand-new instance has no state of its own yet, so state already filed under the folder
        // name it is taking belongs to a version that was removed. This is the second half of that
        // fix and the half that covers what a removal made before it left behind: adoption does not
        // come through here, so a folder BEM lost the metadata for keeps its own state.
        foreach (var moved in InstanceStateFolder.SetAside(StateAreas.Select(area => area.Root), instanceFolder))
        {
            Log?.Invoke(
                $"'{instanceFolder}' takes a folder name BEM already held state under, so that state was "
                + $"moved to '{moved}' rather than handed to this install.");
        }

        WriteRecordTo(instanceFolder, record);

        return new InstalledInstance(instanceFolder, record);
    }

    // Records what a DLC download actually fetched, never what was merely requested. Called only
    // after DlcDownloadFlow reports what landed, so a failed or partial DLC download is recorded as
    // exactly that much, and an instance is never marked as carrying a DLC it does not have. The
    // folder itself never moves: it already holds the base game (and now the DLC) at the path it was
    // registered under, and Write finds that same folder by the record's Id rather than by name.
    public InstalledInstance SetDlc(string instanceId, GameDlcSet dlc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var registry = Registry();
        var record = registry.Find(instanceId)
            ?? throw new InvalidOperationException($"'{instanceId}' is not a known instance, so its DLC could not be recorded.");

        var updated = record with { Dlc = dlc };
        registry.Write(updated);

        var folder = registry.FolderOf(updated)
            ?? throw new InvalidOperationException($"'{instanceId}' has no folder on disk, so its DLC could not be recorded.");

        return new InstalledInstance(folder, updated);
    }

    // Every instance whose recorded DLC set disagrees with its own Modules folder, corrected to what
    // is actually there. SetDlc covers the DLC BEM itself downloads; this covers the DLC that arrived
    // some other way, which for a referenced install means Steam adding one without BEM being asked.
    //
    // Nothing but the Dlc field changes. The record is written back to the folder it was read from
    // rather than to one derived from the corrected name, so an instance whose folder now reads
    // "v1.4.8" while its record says War Sails keeps its saves, its store and its per-version state
    // exactly where they are; and the Id is left alone, because RestingInstanceId and ActiveInstanceId
    // name an instance by that stored string.
    public DlcReconciliationReport ReconcileDlc()
    {
        var changed = new List<DlcReconciliationChange>();

        foreach (var instance in Registry().ReadInstalled())
        {
            if (DlcReconciliation.For(instance.Record) is not { } reconciled)
                continue;

            var updated = instance.Record with { Dlc = reconciled };
            WriteRecordTo(instance.Folder, updated);

            changed.Add(new DlcReconciliationChange(
                updated.Id, InstanceLabel.NameOf(updated), instance.Record.Dlc, reconciled));
        }

        return new DlcReconciliationReport(changed);
    }

    // Every referenced instance whose recorded version disagrees with the game installed under it,
    // corrected to what the files say. A referenced install is Steam's, and Steam patches it or
    // switches its branch without BEM being asked, so the disk is the truth about what is there.
    //
    // Nothing but RecordedGameVersion changes, and the record is written back to the folder it was
    // read from rather than to one derived from the corrected version, so the saves, the store and the
    // per-version state stay where they are; the Id is left alone, because RestingInstanceId and
    // ActiveInstanceId name an instance by that stored string. DataGameVersion is left alone too: the
    // store really was last used with the older version, which is the disagreement the row's drift
    // marker exists to show.
    public GameVersionReconciliationReport ReconcileGameVersions()
    {
        var changed = new List<GameVersionReconciliationChange>();

        foreach (var instance in Registry().ReadInstalled())
        {
            if (GameVersionReconciliation.For(instance.Record) is not { } reconciled)
                continue;

            var updated = instance.Record with { RecordedGameVersion = reconciled };
            WriteRecordTo(instance.Folder, updated);

            changed.Add(new GameVersionReconciliationChange(
                updated.Id,
                InstanceLabel.NameOf(instance.Record),
                instance.Record.RecordedGameVersion,
                reconciled));
        }

        return new GameVersionReconciliationReport(changed);
    }

    // A name the user chose, written to the record and then carried into the folder name, because a
    // folder that did not say which instance it held was the whole complaint the chosen name exists to
    // answer. The record is written first and the folder moves after: the write finds the folder by
    // the record's Id, so a rename that could not move the folder leaves the name recorded and the
    // folder to be brought into line by the next start rather than losing the name over a held folder.
    // An empty or whitespace name clears the chosen name and the row and the folder both go back to
    // deriving their own.
    public InstalledInstance Rename(string instanceId, string? chosenName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var registry = Registry();
        var record = registry.Find(instanceId)
            ?? throw new InvalidOperationException($"'{instanceId}' is not a known instance, so it could not be renamed.");

        var result = InstanceNaming.Read(chosenName);

        if (!result.Accepted)
        {
            throw new InvalidOperationException(
                $"A name longer than {InstanceNaming.MaxLength} characters was not written down.");
        }

        var updated = record with { ChosenName = result.Stored };
        registry.Write(updated);

        AlignFolderNames(updated.Id);

        return Aligned(updated.Id)
            ?? throw new InvalidOperationException($"'{instanceId}' has no folder on disk, so it could not be renamed.");
    }

    // The instance as it stands after a folder alignment, read back rather than assembled: the folder
    // is not the one the caller knew, and the record's own GameFolder moved with it.
    private InstalledInstance? Aligned(string instanceId) => Registry().ReadInstalled()
        .FirstOrDefault(instance => string.Equals(instance.Record.Id, instanceId, StringComparison.Ordinal));

    // What this instance is for, written to the record and then carried into the folder name, so the
    // games root says which of its folders are throwaways for testing a mod and which are not. A mod's
    // build tool reads the purpose straight out of instance.json, so nothing here is allowed to depend
    // on BEM being the one asking, and the folder rename is the one thing that happens afterwards: the
    // declaration is written first and stands whether or not the folder could move.
    //
    // The resting instance is refused outright rather than being allowed to declare itself: it is the
    // user's own game by definition, and the one failure this whole field exists to prevent is a mod
    // build landing in it. Its record keeps whatever it last said, because a resting change is not a
    // statement about what the instance is for forever, and InstanceLabel.For already reads it as
    // gameplay for as long as it rests.
    public InstalledInstance SetPurpose(string instanceId, InstancePurpose purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        if (InstanceLabel.IsResting(instanceId, RestingInstanceId))
        {
            throw new InvalidOperationException(
                $"'{instanceId}' is the resting instance, which is for playing, so its purpose cannot be declared.");
        }

        var registry = Registry();
        var record = registry.Find(instanceId)
            ?? throw new InvalidOperationException($"'{instanceId}' is not a known instance, so its purpose could not be set.");

        var updated = record with { Purpose = purpose };
        registry.Write(updated);

        AlignFolderNames(updated.Id);

        return Aligned(updated.Id)
            ?? throw new InvalidOperationException($"'{instanceId}' has no folder on disk, so its purpose could not be set.");
    }

    // Which version's user data sits at the canonical machine paths between launches. BEM's own state
    // about a version is filed by that position rather than by the version's identity, so it has to
    // travel with the change or the incoming version inherits the outgoing one's pins, profiles,
    // dividers, accepted findings and risks, launch history, settings attributions and load order
    // backups, while the version that wrote them reads an empty folder.
    //
    // The files move first and the setting is written last, because the setting is one atomic little
    // write and the move is the part that can fail: a move that fails puts itself back and leaves the
    // resting version exactly as it was, and a setting that fails after a good move undoes the move
    // rather than leaving the two disagreeing about which version reads what.
    //
    // What this does not do is move any user data. Which folder holds the saves is the launch's
    // concern, and making a different version resting while its data is not at the canonical paths is
    // a separate step with its own report.
    public RestingChangeResult SetResting(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var incoming = Registry().ReadInstalled()
            .FirstOrDefault(instance => string.Equals(instance.Record.Id, instanceId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"'{instanceId}' is not a known instance, so it cannot become the resting instance.");

        if (string.Equals(instanceId, RestingInstanceId, StringComparison.Ordinal))
            return new RestingChangeResult(false, false,
                Strings.Current.Format("Core.Instances.Manager.RestingUnchanged", InstanceLabel.NameOf(incoming.Record)));

        var moved = InstanceStateRelocation.Swap(StateAreas, StateKeyOf(RestingInstanceId), StateKeyOf(incoming));

        try
        {
            WriteSettings(current => current with { RestingInstanceId = instanceId });
        }
        catch
        {
            InstanceStateRelocation.Undo(moved);
            throw;
        }

        // The folder name follows the role, so both folders are out of line the moment the setting is
        // written: the incoming version wants the resting name and the outgoing one wants its version
        // back. The ordinary alignment does both, and takes the outgoing version's state - which the
        // swap above has just filed under the folder name it is about to lose - along with it. It runs
        // after the setting rather than before, because the setting is what says which name each one
        // wants, and a failure here leaves the state correct and two folders wearing the wrong names,
        // which the next start puts right.
        AlignFolderNames();

        return DescribeRestingChange(Aligned(instanceId) ?? incoming);
    }

    // What the user has to be told, read off disk rather than assumed. The store of a version that is
    // resting is empty at rest, because its data is the canonical paths themselves. A store that still
    // holds files says the user data did not come with the change: launching this version would run it
    // against whatever is at the canonical paths, and launching any other version is refused by the
    // move that would have to put its data there.
    private RestingChangeResult DescribeRestingChange(InstalledInstance incoming)
    {
        var store = InstanceLayout.UserDataFolder(incoming.Folder);

        var populated = paths.Pairs.Any(pair => HasEntries(
            InstanceLayout.StoreFolder(incoming.Folder, pair.StoreFolderName)));

        if (!populated)
        {
            return new RestingChangeResult(true, false,
                Strings.Current.Format("Core.Instances.Manager.RestingChanged", InstanceLabel.NameOf(incoming.Record)));
        }

        return new RestingChangeResult(true, true,
            Strings.Current.Format("Core.Instances.Manager.RestingChangedDataStranded", InstanceLabel.NameOf(incoming.Record), store));
    }

    private static bool HasEntries(string path)
    {
        try
        {
            return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string? StateKeyOf(string? instanceId) =>
        instanceId is null
            ? null
            : Registry().ReadInstalled()
                .FirstOrDefault(instance => string.Equals(instance.Record.Id, instanceId, StringComparison.Ordinal))
                is { } found
                ? StateKeyOf(found)
                : null;

    private static string? StateKeyOf(InstalledInstance instance) =>
        InstanceStateFolder.KeyFor(InstanceDataRoot.ForInstance(instance.Folder));

    // The instance the next launch runs, independent of which one sits at rest. Nothing here checks
    // whether a game is running: that refusal belongs to the caller, which is the only side that
    // knows a process is alive.
    public void SetActive(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var registry = Registry();

        if (registry.Find(instanceId) is null)
            throw new InvalidOperationException($"'{instanceId}' is not a known instance, so it cannot become the active instance.");

        WriteSettings(current => current with { ActiveInstanceId = instanceId });
    }

    // Removing a version has to be able to say the active one is gone. Nothing else clears it, and
    // the id stays in settings.json across a restart, so a removal that skipped this would leave
    // Launch, Dry Run and bisection failing until the user picked another version on Play.
    public void ClearActive() => WriteSettings(current => current with { ActiveInstanceId = string.Empty });

    // targetKind decides whether the junctions this launch would hold can safely be released when
    // the launched process exits. BlseStandalone and GameExecutable are the game itself, so holding
    // the junctions until that process exits is correct. Every other kind (Steam, the TaleWorlds
    // launcher, the two BLSE launcher shims) hands off to a game process this call never sees and
    // exits on its own almost immediately, so if this were allowed to proceed the junctions would be
    // torn down while the game was still starting or running, and the game would read and write the
    // resting instance's data instead of the one the user picked - the exact isolation break this
    // whole feature exists to prevent. Launching the resting instance itself is exempt: no junctions
    // are ever created for it, so no target can drop them.
    public async Task<ActivationResult> LaunchAsync(
        string instanceId, LaunchTargetKind targetKind, Func<CancellationToken, Task> launch, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(launch);

        var isRestingInstance = string.Equals(instanceId, RestingInstanceId, StringComparison.Ordinal);

        if (!isRestingInstance && !LaunchRecord.CanSeeTheEnd(targetKind))
        {
            throw new InvalidOperationException(
                $"'{LaunchTargetResolver.DisplayNameFor(targetKind)}' hands off to the game and exits, so BEM "
                + "cannot keep this version's data isolated for the whole run; the launch was refused before "
                + $"anything was junctioned. Launch via '{LaunchTargetResolver.DisplayNameFor(LaunchTargetKind.BlseStandalone)}' instead.");
        }

        return await RunUnderInstanceAsync(instanceId, launch, cancellationToken);
    }

    // The same junctions a launch gets, for work that starts the game without being a launch: the dry
    // run and both bisection modes. Started through the orchestrator directly, they ran the game with
    // no junctions at all, so on any version that is not the resting one the game read and wrote the
    // resting version's saves, settings, logs and shader cache while the result was reported against
    // the version the user had selected.
    //
    // There is no target-kind refusal here, and that is the whole difference from LaunchAsync.
    // LaunchAsync refuses a hand-off target because it drops the junctions the moment its own delegate
    // returns, which for a launcher is a second or two after the game started. This scope is held for
    // the length of the caller's whole operation instead: a dry run's orchestrator awaits the game
    // process itself, and a guided bisection's scope spans every experiment and the user's answer to
    // each of them, so even a launcher-mediated start inside one stays covered while the game is up.
    //
    // One scope per operation, never one per game start. ActivationLock is a file handle held with
    // FileShare.None, so a second activation inside a live one is refused rather than nested; and a
    // bisection that tore the junctions down between experiments would move the whole Documents folder
    // across volumes twice per step and open a window between steps where a failure could strand a
    // save. Teardown is symmetric either way: ActivationSession restores every path it applied whether
    // the body returns, throws or is canceled.
    public async Task<ActivationResult> RunUnderInstanceAsync(
        string instanceId, Func<CancellationToken, Task> body, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(body);

        // Every entry point that activates an instance arrives here, so the refusal belongs here
        // rather than on the one button that was taught it. A teardown running past its budget still
        // holds the activation lock, and a second activation inside it reaches ActivationLock.Acquire
        // and comes back as an access-denied error the caller reports as a read-only folder or
        // antivirus. Play's Launch guards itself and says so in its status line; the Saves launch, the
        // dry run and both bisection modes did not, and this is what makes the three agree.
        if (ActivationHeldHere)
        {
            throw new InvalidOperationException(
                "BEM is still putting the canonical folders back after the last run, so nothing can be "
                + "activated until that finishes. Wait for it to end and try again.");
        }

        var registry = Registry();
        var restingFolder = RequireRestingFolder();

        var target = registry.Find(instanceId)
            ?? throw new InvalidOperationException($"'{instanceId}' is not a known instance, so it could not be launched.");

        var targetFolder = registry.FolderOf(target)
            ?? throw new InvalidOperationException($"'{instanceId}' has no folder on disk, so it could not be launched.");

        return await new ActivationSession(paths, restingFolder, TeardownBudget, Log)
            .RunAsync(targetFolder, body, cancellationToken);
    }

    private static readonly JsonSerializerOptions MetadataFormat = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private InstanceRegistry Registry() => new(settings.Read().GamesRoot);

    // Mirrors InstanceRegistry's own atomic write, which is private and always derives its folder
    // from the record's display name. Register needs to write to a folder the caller already picked.
    private static void WriteRecordTo(string instanceFolder, InstanceRecord record)
    {
        Directory.CreateDirectory(instanceFolder);

        var path = InstanceLayout.MetadataPath(instanceFolder);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, MetadataFormat));
        File.Move(temporary, path, overwrite: true);
    }

    private string RequireRestingFolder()
    {
        var restingId = RestingInstanceId
            ?? throw new InvalidOperationException(
                "No resting instance is set yet, so nothing can be launched or repaired. Adopt the current install first.");

        var registry = Registry();
        var resting = registry.Find(restingId)
            ?? throw new InvalidOperationException($"The resting instance '{restingId}' is no longer a known instance.");

        return registry.FolderOf(resting)
            ?? throw new InvalidOperationException($"The resting instance '{restingId}' has no folder on disk.");
    }

    private void WriteSettings(Func<InstanceSettings, InstanceSettings> change) =>
        settings.Write(change(settings.Read()));

    private static InstanceRecord NewRecord(
        InstanceRegistry registry, string gameFolder, string displayName, string branch,
        string buildId, bool gameFolderIsReferenced, GameDlcSet variant, string? chosenName = null,
        InstancePurpose purpose = InstancePurpose.Unspecified)
    {
        var version = GameVersionReader.Read(gameFolder).ToString();

        return new InstanceRecord(
            registry.MintId(displayName, branch, variant),
            displayName,
            branch,
            buildId,
            gameFolder,
            gameFolderIsReferenced,
            version,
            version,
            DateTimeOffset.UtcNow,
            null,
            ChosenName: InstanceNaming.Read(chosenName).Stored,
            Purpose: purpose);
    }
}
