using System.Globalization;
using System.Text.Json;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Settings;

namespace BannerlordEnvironmentManager.Core.DryRun;

public enum WatchStartStatus
{
    Started,

    // The companion is in place and the state file is written, but no game was started here. This is
    // what the Play tab's Launch button and the Watch page's own arming button produce: the
    // launch itself happens on the user's usual path, with the companion added to that one run.
    Armed,

    AlreadyWatching,
    NoInstall,
    NoHarmony,
    NoPayload,
    NoTarget,
    InstallFailed,
    LaunchFailed,
    NotWatching,

    // The companion fits on the module list and the command line carrying it does not fit in the
    // buffer the game copies it into, so the launch would die about two seconds in leaving nothing
    // behind. Refused before anything is installed rather than discovered afterwards.
    WouldNotFitTheCommandLine
}

public enum WatchStopStatus
{
    Removed,
    NotWatching,
    RemovalFailed
}

// PreferredTarget, ExtraArguments and CrashHandling are the user's own launch settings. A watch
// session is a real play session, so it has to be the configuration they actually play, not one
// chosen for the companion's convenience.
public sealed record WatchRequest(
    string GameInstallPath,
    IReadOnlyList<ModuleEntry> EnabledModules,
    string CompanionPayloadPath,
    string ExtraArguments = "",
    LaunchTargetKind? PreferredTarget = null,
    BlseCrashHandlerOptions? CrashHandling = null);

public sealed record WatchStartResult(
    WatchStartStatus Status,
    string Message,
    string RunId = "",
    LaunchTarget? Target = null)
{
    public bool Started => Status == WatchStartStatus.Started;
}

public sealed record WatchStopResult(WatchStopStatus Status, string Message)
{
    public bool Removed => Status == WatchStopStatus.Removed;
}

public sealed record WatchState(string RunId, string GameInstallPath, DateTime StartedUtc)
{
    public bool IsEmpty => RunId.Length == 0;

    public static WatchState None => new(string.Empty, string.Empty, default);
}

// Starts the game with BEM's companion in it for a whole play session, and takes it out again.
//
// This is not the dry run. The dry run installs the companion, exits the game at the first frame and
// removes the companion in a finally. Here the game keeps running for as long as the player plays,
// the companion stays on disk until it is removed deliberately, and BEM does not wait on the process
// or restore any config afterwards, because a real session's own settings changes are the player's.
public sealed class WatchSession(string stateFilePath)
{
    // Only these two targets carry a module list on their command line, and the companion has to be
    // in that list to load at all. The launcher targets read LauncherData.xml instead, and putting
    // BEM's own module into the saved load order to reach them would leave it there.
    private static readonly LaunchTargetKind[] ModuleListTargets =
    [
        LaunchTargetKind.BlseStandalone,
        LaunchTargetKind.GameExecutable
    ];

    public static string DefaultStateFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        "watch-session.json");

    public string StateFilePath { get; } = stateFilePath;

    public WatchState Read()
    {
        try
        {
            if (!File.Exists(StateFilePath))
                return WatchState.None;

            return JsonSerializer.Deserialize<WatchState>(File.ReadAllText(StateFilePath)) ?? WatchState.None;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return WatchState.None;
        }
    }

    // True only when BEM armed a watch and the companion it installed is still there. A companion
    // folder left behind by a failed dry run is inert without a marker argument, so it is not a
    // watch session and must not be reported as one.
    public bool IsWatching(string gameInstallPath) =>
        !Read().IsEmpty && new CompanionInstaller(gameInstallPath).IsInstalled;

    // The user asked for a watch and no game is being started right now. Arming survives a restart
    // because it is a file, and it stays armed until it is stopped.
    public bool IsArmed => !Read().IsEmpty;

    // Arming on its own, so the launch can be the user's usual one. It installs the companion and
    // writes the state file, and it is deliberately idempotent: an armed watch whose companion folder
    // was deleted from outside BEM is put back rather than reported as broken, and the run id and the
    // time it was armed are kept so stopping still removes exactly what arming installed.
    public WatchStartResult Arm(WatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var state = Read();
        var installer = new CompanionInstaller(request.GameInstallPath);

        // IsUnderLegacyName rather than IsInstalled: a watch armed by a build of BEM from before the
        // companion was renamed still counts as installed, but arming again is what replaces the old
        // folder with the current one, so it must not be short-circuited here.
        if (!state.IsEmpty && installer.IsInstalled && !installer.IsUnderLegacyName)
        {
            return new WatchStartResult(
                WatchStartStatus.AlreadyWatching,
                Strings.Current.Format(
                    "Core.DryRun.Watch.Arm.AlreadyWatching",
                    state.StartedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                    state.RunId),
                state.RunId);
        }

        if (Refuse(request) is { } refusal)
            return refusal;

        // Arming installs a module and promises the next launch is watched, and a promise that ends in
        // a two-second fastfail is worse than saying no here.
        if (RefuseForCommandLine(ResolveTarget(request)) is { } tooLong)
            return tooLong;

        var install = installer.Install(request.CompanionPayloadPath);

        if (!install.Installed)
        {
            return new WatchStartResult(
                WatchStartStatus.InstallFailed,
                install.Reason ?? Strings.Current["Core.DryRun.Orchestrator.Refused.CompanionNotInstalled"]);
        }

        var runId = state.IsEmpty ? DryRunPaths.NewRunId() : state.RunId;

        Write(new WatchState(runId, request.GameInstallPath, state.IsEmpty ? DateTime.UtcNow : state.StartedUtc));

        // Said at the moment of arming rather than discovered at the moment of launching. A watch that
        // can never fire on this install is worth knowing about before the user plays a session
        // expecting a timeline out of it.
        var reachable = WatchedLaunch.CanBeWatched(request.GameInstallPath)
            ? string.Empty
            : Strings.Current["Core.DryRun.Watch.Arm.NotReachable"];

        return new WatchStartResult(
            WatchStartStatus.Armed,
            Strings.Current.Format("Core.DryRun.Watch.Arm.Armed", installer.ModuleFolder) + reachable,
            runId);
    }

    public WatchStartResult Start(WatchRequest request, WatchLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(launch);

        var state = Read();
        var installer = new CompanionInstaller(request.GameInstallPath);

        if (!state.IsEmpty && installer.IsInstalled && !installer.IsUnderLegacyName)
        {
            return new WatchStartResult(
                WatchStartStatus.AlreadyWatching,
                Strings.Current.Format(
                    "Core.DryRun.Watch.Start.AlreadyWatching",
                    state.StartedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                    state.RunId),
                state.RunId);
        }

        if (Refuse(request) is { } refusal)
            return refusal;

        var runId = DryRunPaths.NewRunId();
        var target = ResolveTarget(request);

        if (target is null)
        {
            return new WatchStartResult(
                WatchStartStatus.NoTarget,
                Strings.Current.Format("Core.DryRun.Watch.Start.NoTarget", request.GameInstallPath));
        }

        if (RefuseForCommandLine(target) is { } tooLong)
            return tooLong;

        var install = installer.Install(request.CompanionPayloadPath);

        if (!install.Installed)
        {
            return new WatchStartResult(
                WatchStartStatus.InstallFailed,
                install.Reason ?? Strings.Current["Core.DryRun.Orchestrator.Refused.CompanionNotInstalled"]);
        }

        Write(new WatchState(runId, request.GameInstallPath, DateTime.UtcNow));

        try
        {
            launch(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The companion is out again immediately: leaving it installed after a launch that never
            // happened would put a module in the game folder for a session nobody is watching. Whether
            // that removal worked is the whole point of the sentence, so it is not discarded.
            var removed = installer.Remove() || installer.RemainingFolders.Count == 0;
            Clear();

            return new WatchStartResult(
                WatchStartStatus.LaunchFailed,
                Strings.Current.Format("Core.DryRun.Watch.Start.LaunchFailedBase", ex.Message)
                + (removed
                    ? Strings.Current["Core.DryRun.Watch.Start.LaunchFailedRemoved"]
                    : Strings.Current.Format(
                        "Core.DryRun.Watch.Start.LaunchFailedStillThere", installer.ModuleFolder)));
        }

        return new WatchStartResult(
            WatchStartStatus.Started,
            Strings.Current.Format(
                "Core.DryRun.Watch.Start.Started", runId, target.DisplayName, installer.ModuleFolder),
            runId,
            target);
    }

    // Another watched launch while the watch is already armed, with the companion already in place.
    //
    // This exists because "every launch is watched until you stop" was only ever true of launches
    // BEM starts. The companion has to be on the command line's module list to load at all, and a
    // launch from Steam, a shortcut or the Play tab does not carry it. Rather than write BEM's
    // own module into the saved load order, which would leave it there, the second, third and fourth
    // watched launch are started from here.
    //
    // Each one is its own run: the companion derives a run id per process from the armed session's id,
    // so one session's trail never overwrites another's, and none of that has to travel on the command
    // line.
    public WatchStartResult Relaunch(WatchRequest request, WatchLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(launch);

        var installer = new CompanionInstaller(request.GameInstallPath);
        var armed = Read();

        if (armed.IsEmpty || !installer.IsInstalled)
        {
            return new WatchStartResult(
                WatchStartStatus.NotWatching, Strings.Current["Core.DryRun.Watch.Relaunch.NotWatching"]);
        }

        if (Refuse(request) is { } refusal)
            return refusal;

        var target = ResolveTarget(request);

        if (target is null)
        {
            return new WatchStartResult(
                WatchStartStatus.NoTarget,
                Strings.Current.Format("Core.DryRun.Watch.Relaunch.NoTarget", request.GameInstallPath));
        }

        if (RefuseForCommandLine(target) is { } tooLong)
            return tooLong;

        try
        {
            launch(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The companion stays installed and the watch stays armed: this failed to start a game,
            // it did not un-arm anything, and saying otherwise would be a message that overstates.
            return new WatchStartResult(
                WatchStartStatus.LaunchFailed,
                Strings.Current.Format("Core.DryRun.Watch.Relaunch.LaunchFailed", ex.Message));
        }

        return new WatchStartResult(
            WatchStartStatus.Started,
            Strings.Current.Format("Core.DryRun.Watch.Relaunch.Started", target.DisplayName, armed.RunId),
            armed.RunId,
            target);
    }

    // Reversal. A removal that fails is reported with the folder it failed on: a watch the user
    // believes they turned off, that is still installed, is the worst outcome available here.
    //
    // savedOrder is the user's LauncherData.xml, and it is passed so that stopping can take BEM's own
    // module out of it. Without that, a load order that names BEM.Companion outlives the folder and
    // points at nothing. Nothing else in that file is touched, and passing nothing means it is not
    // opened at all.
    public WatchStopResult Stop(string gameInstallPath, LauncherDataStore? savedOrder = null)
    {
        var state = Read();
        var installer = new CompanionInstaller(gameInstallPath);
        var folders = installer.InstalledFolders;
        var wasInstalled = folders.Count > 0;

        if (state.IsEmpty && !wasInstalled)
            return new WatchStopResult(WatchStopStatus.NotWatching, Strings.Current["Core.DryRun.Watch.Stop.NotWatching"]);

        if (wasInstalled && !installer.Remove() && installer.RemainingFolders is { Count: > 0 } stuck)
        {
            return new WatchStopResult(
                WatchStopStatus.RemovalFailed,
                Strings.Current.Format("Core.DryRun.Watch.Stop.RemovalFailed", string.Join("' and '", stuck)));
        }

        Clear();

        var pruned = PruneSavedOrder(savedOrder);

        return new WatchStopResult(
            WatchStopStatus.Removed,
            (wasInstalled
                ? Strings.Current.Format("Core.DryRun.Watch.Stop.Removed", string.Join("' and '", folders))
                : Strings.Current["Core.DryRun.Watch.Stop.AlreadyGone"])
            + pruned);
    }

    private static string PruneSavedOrder(LauncherDataStore? savedOrder)
    {
        if (savedOrder is null)
            return string.Empty;

        try
        {
            return CompanionLoadOrder.Prune(savedOrder) is { Count: > 0 } removed
                ? Strings.Current.Format("Core.DryRun.Watch.Prune.Removed", string.Join(", ", removed))
                : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Strings.Current.Format("Core.DryRun.Watch.Prune.Failed", ex.Message, savedOrder.FilePath);
        }
    }

    private static WatchStartResult? Refuse(WatchRequest request)
    {
        if (!GameInstallLocator.IsValidInstall(request.GameInstallPath))
        {
            return new WatchStartResult(
                WatchStartStatus.NoInstall,
                Strings.Current.Format("Core.DryRun.Orchestrator.Refused.NotRecognized", request.GameInstallPath));
        }

        // The companion is compiled against the user's own 0Harmony and deliberately ships none of
        // its own, so without Bannerlord.Harmony enabled it cannot load at all.
        if (!request.EnabledModules.Any(m =>
                string.Equals(m.Id.Value, CompanionManifest.HarmonyModuleId, StringComparison.OrdinalIgnoreCase)))
        {
            return new WatchStartResult(
                WatchStartStatus.NoHarmony,
                Strings.Current.Format("Core.DryRun.Watch.Refuse.HarmonyRequired", CompanionManifest.HarmonyModuleId));
        }

        if (!File.Exists(request.CompanionPayloadPath))
        {
            return new WatchStartResult(
                WatchStartStatus.NoPayload,
                Strings.Current.Format(
                    "Core.DryRun.Orchestrator.Refused.PayloadMissing", request.CompanionPayloadPath));
        }

        return null;
    }

    // The game copies the whole argument string into a fixed buffer with strcpy_s and fails fast when
    // it does not fit, about two seconds in, before any managed code runs. See GameCommandLine for
    // the measurement. Watching adds the companion's module id and nothing else, so this is the one
    // refusal that is about BEM's own addition rather than about the user's load order, and it says
    // which.
    private static WatchStartResult? RefuseForCommandLine(LaunchTarget? target)
    {
        if (target is null || GameCommandLine.Measure(target) is not { Fits: false } budget)
            return null;

        return new WatchStartResult(
            WatchStartStatus.WouldNotFitTheCommandLine,
            Strings.Current.Format(
                "Core.DryRun.Watch.Refuse.CommandLineOverflow",
                budget.Length,
                budget.Limit,
                budget.Overflow,
                WatchedLaunch.CompanionCharacterCost));
    }

    private static LaunchTarget? ResolveTarget(WatchRequest request)
    {
        // Whichever name the companion is installed under. Relaunch runs against a companion that is
        // already on disk, and that can be a folder an older build of BEM wrote under the old name;
        // naming the current id there would resolve to nothing and record an empty session. Nothing is
        // installed yet when Start calls this, and Start installs the current name, so the fallback is
        // right for it.
        var installed = new CompanionInstaller(request.GameInstallPath).InstalledModuleId;
        var modules = WatchedLaunch.WithCompanion(request.EnabledModules, installed ?? CompanionManifest.ModuleId);
        var crashHandling = request.CrashHandling ?? BlseCrashHandlerOptions.Default;

        LaunchTarget? Build(LaunchTargetKind kind) =>
            LaunchTargetResolver.Resolve(
                request.GameInstallPath, kind, modules, request.ExtraArguments, crashHandling) is { } built
            && built.Kind == kind
                ? built
                : null;

        if (request.PreferredTarget is { } preferred
            && ModuleListTargets.Contains(preferred)
            && Build(preferred) is { } chosen)
        {
            return chosen;
        }

        foreach (var kind in ModuleListTargets)
        {
            if (Build(kind) is { } fallback)
                return fallback;
        }

        return null;
    }

    private void Write(WatchState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(state));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A watch whose state file could not be written still runs; it is the next session's
            // status line that suffers, not this one.
        }
    }

    private void Clear()
    {
        try
        {
            if (File.Exists(StateFilePath))
                File.Delete(StateFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

// Starts the game and returns. Unlike the dry run's launcher this never waits on the process and
// never kills it: the session belongs to the player.
public delegate void WatchLaunch(LaunchTarget target);

public static class WatchProcessLauncher
{
    public static WatchLaunch Create() => target =>
    {
        ArgumentNullException.ThrowIfNull(target);

        // UseShellExecute so the game gets its own process group and outlives BEM, and so a target
        // that is a URI rather than a file would still work if one is ever added here.
        _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = target.Path,
            Arguments = target.Arguments,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(target.Path) ?? string.Empty
        }) ?? throw new InvalidOperationException($"'{target.Path}' did not start.");
    };
}
