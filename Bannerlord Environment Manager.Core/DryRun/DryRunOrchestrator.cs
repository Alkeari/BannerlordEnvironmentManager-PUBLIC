using System.Diagnostics;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Settings;

namespace BannerlordEnvironmentManager.Core.DryRun;

public enum DryRunStatus
{
    Booted,
    ModuleThrew,
    StoppedDuringLoad,
    NoCapture,
    Refused,
    Canceled,
    TimedOut,
    Failed
}

public sealed record DryRunProgress(
    int Completed,
    int? Total,
    string? CurrentModule,
    TimeSpan Elapsed,
    string Message);

// PreferredTarget and CrashHandling are the user's own launch settings, and they are here because a
// dry run that tests a configuration the user never runs is worse than no dry run: it produces
// crashes the real launch does not have. Left null, the dry run picks the same target a normal
// launch would have picked for itself.
public sealed record DryRunRequest(
    string GameInstallPath,
    IReadOnlyList<ModuleEntry> EnabledModules,
    string CompanionPayloadPath,
    string LauncherDataPath,
    string ConfigsFolderPath,
    string ExtraArguments = "",
    LaunchTargetKind? PreferredTarget = null,
    BlseCrashHandlerOptions? CrashHandling = null);

public sealed record DryRunVerdict(
    DryRunStatus Status,
    string Summary,
    DryRunAttribution Attribution,
    bool IsDegraded,
    string DegradedReason,
    int? SubModuleCount,
    IReadOnlyList<DryRunModuleOutcome> FailedModules,
    string? ModuleLoadingWhenStopped,
    TimeSpan Elapsed,
    IReadOnlyList<string> ConfigChanges,
    DryRunResult? Result,
    BreadcrumbTrail Trail,
    string RunId,
    string? ResultPath,
    string? BreadcrumbPath,
    // Which executable actually ran. A dry run through a different target from the one the user
    // plays through is a different configuration, so the verdict says which one it was.
    LaunchTargetKind? LaunchTarget = null,
    // What the config safety net actually did, including the files it could not copy and so could
    // not stand behind. Null only for a run that never got as far as taking a snapshot.
    LaunchConfigRestoreResult? ConfigOutcome = null,
    // The folder BEM's own companion module is still sitting in, when taking it back out failed.
    // Empty on every run that removed it, which is every run that was not blocked on the file.
    string CompanionLeftBehind = "");

// Returns when the game process has exited. Canceling the token must kill it.
public delegate Task DryRunLaunch(LaunchTarget target, CancellationToken cancellationToken);

// outputRoot must be DryRunPaths.GetDefaultRoot() outside tests: the companion resolves the same
// folder from LOCALAPPDATA on its own and knows nothing about BEM, so any other root means BEM
// watches a folder nothing is ever written to.
public sealed class DryRunOrchestrator(
    string outputRoot,
    string snapshotRoot,
    DryRunLaunch launch,
    TimeSpan? pollInterval = null,
    TimeSpan? timeout = null)
{
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromMilliseconds(250);

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(6);

    public async Task<DryRunVerdict> RunAsync(
        DryRunRequest request,
        IProgress<DryRunProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var runId = DryRunPaths.NewRunId();
        var breadcrumbPath = DryRunPaths.BreadcrumbPath(outputRoot, runId);
        var resultPath = DryRunPaths.ResultPath(outputRoot, runId);

        if (Refuse(request, runId) is { } refusal)
            return refusal;

        var installer = new CompanionInstaller(request.GameInstallPath);
        var clock = Stopwatch.StartNew();
        LaunchConfigSnapshot? snapshot = null;
        LaunchConfigRestoreResult? configOutcome = null;

        var status = DryRunStatus.NoCapture;
        var failureMessage = string.Empty;
        var companionLeftBehind = string.Empty;
        LaunchTargetKind? launchTargetKind = null;

        try
        {
            snapshot = LaunchConfigSnapshot.Capture(
                request.LauncherDataPath, request.ConfigsFolderPath, Path.Combine(snapshotRoot, runId));

            var install = installer.Install(request.CompanionPayloadPath);

            if (!install.Installed)
            {
                return Refused(
                    runId,
                    install.Reason ?? Strings.Current["Core.DryRun.Orchestrator.Refused.CompanionNotInstalled"],
                    clock.Elapsed);
            }

            var target = ResolveTarget(request, runId);

            if (target is null)
            {
                return Refused(
                    runId,
                    Strings.Current.Format("Core.DryRun.Orchestrator.Refused.NoTarget", request.GameInstallPath),
                    clock.Elapsed);
            }

            // The game copies the whole argument string into a fixed buffer with strcpy_s and fails
            // fast when it does not fit, two seconds in, before any managed code runs. A dry run that
            // sent it would capture nothing and look exactly like a load order that crashes on boot,
            // which is the answer this whole feature exists to give correctly.
            if (GameCommandLine.Measure(target) is { Fits: false } budget)
            {
                return Refused(
                    runId,
                    Strings.Current.Format(
                        "Core.DryRun.Orchestrator.Refused.CommandLineOverflow",
                        budget.Length,
                        budget.Limit,
                        budget.Overflow,
                        Launcher.WatchedLaunch.CompanionCharacterCost + CompanionManifest.MarkerPrefix.Length + runId.Length + 1,
                        Launcher.WatchedLaunch.CompanionCharacterCost),
                    clock.Elapsed);
            }

            launchTargetKind = target.Kind;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);

            Task launchTask;

            try
            {
                launchTask = launch(target, deadline.Token) ?? Task.CompletedTask;
            }
            catch (Exception ex)
            {
                launchTask = Task.FromException(ex);
            }

            while (!launchTask.IsCompleted)
            {
                Report(progress, breadcrumbPath, clock.Elapsed);
                await Task.WhenAny(launchTask, Task.Delay(_poll)).ConfigureAwait(false);
            }

            try
            {
                await launchTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                status = cancellationToken.IsCancellationRequested ? DryRunStatus.Canceled : DryRunStatus.TimedOut;
            }
            catch (Exception ex)
            {
                status = DryRunStatus.Failed;
                failureMessage = ex.Message;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status = DryRunStatus.Failed;
            failureMessage = ex.Message;
        }
        finally
        {
            // A companion BEM could not take out again is a module of BEM's own left in the game the
            // owner is about to play, which is the one thing the marker argument exists to prevent.
            if (!installer.Remove() && Directory.Exists(installer.ModuleFolder))
                companionLeftBehind = installer.ModuleFolder;

            if (snapshot is not null)
            {
                configOutcome = snapshot.Restore();
                snapshot.Dispose();
            }
        }

        var trail = BreadcrumbFile.Read(breadcrumbPath);
        var result = DryRunResultFile.Read(resultPath);

        return Describe(
            runId, status, failureMessage, result, trail, configOutcome, clock.Elapsed, resultPath, breadcrumbPath,
            launchTargetKind, companionLeftBehind);
    }

    // Only two targets carry a module list on their command line, so only those two can run a chosen
    // load order. Within that, the user's own choice wins: a dry run through Bannerlord.exe when the
    // user always plays through BLSE is a different configuration, with no BLSE assembly resolver,
    // no interceptor and no BLSE crash handler, and every mod that needs any of those fails in it
    // and only in it.
    private static readonly LaunchTargetKind[] ModuleListTargets =
    [
        LaunchTargetKind.BlseStandalone,
        LaunchTargetKind.GameExecutable
    ];

    private static LaunchTarget? ResolveTarget(DryRunRequest request, string runId)
    {
        var modules = BuildModuleList(request.EnabledModules);
        var arguments = BuildArguments(runId, request.ExtraArguments);
        var crashHandling = request.CrashHandling ?? BlseCrashHandlerOptions.Default;

        LaunchTarget? Build(LaunchTargetKind kind) =>
            LaunchTargetResolver.Resolve(request.GameInstallPath, kind, modules, arguments, crashHandling) is { } built
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

    private DryRunVerdict? Refuse(DryRunRequest request, string runId)
    {
        if (!GameInstallLocator.IsValidInstall(request.GameInstallPath))
        {
            return Refused(
                runId,
                Strings.Current.Format("Core.DryRun.Orchestrator.Refused.NotRecognized", request.GameInstallPath),
                TimeSpan.Zero);
        }

        // The companion is compiled against the user's own 0Harmony and deliberately ships none of
        // its own, so without Bannerlord.Harmony enabled it cannot load at all.
        if (!request.EnabledModules.Any(m =>
                string.Equals(m.Id.Value, CompanionManifest.HarmonyModuleId, StringComparison.OrdinalIgnoreCase)))
        {
            return Refused(
                runId,
                Strings.Current.Format(
                    "Core.DryRun.Orchestrator.Refused.HarmonyRequired", CompanionManifest.HarmonyModuleId),
                TimeSpan.Zero);
        }

        if (!File.Exists(request.CompanionPayloadPath))
        {
            return Refused(
                runId,
                Strings.Current.Format("Core.DryRun.Orchestrator.Refused.PayloadMissing", request.CompanionPayloadPath),
                TimeSpan.Zero);
        }

        return null;
    }

    private static DryRunVerdict Refused(string runId, string reason, TimeSpan elapsed) => new(
        DryRunStatus.Refused, reason, DryRunAttribution.Unknown, false, string.Empty, null, [], null,
        elapsed, [], null, BreadcrumbTrail.Empty, runId, null, null);

    // The same insertion a watched launch uses, and deliberately the same code: it used to be a second
    // copy of the loop, and a copy is a second place for the bug that put BEM's own module on one
    // command line twice. A dry run installs the companion under its current name immediately before
    // this, so the current id is the right one here.
    private static IReadOnlyList<ModuleEntry> BuildModuleList(IReadOnlyList<ModuleEntry> enabled) =>
        Launcher.WatchedLaunch.WithCompanion(enabled);

    private static string BuildArguments(string runId, string extra)
    {
        var marker = CompanionManifest.MarkerPrefix + runId;

        return string.IsNullOrWhiteSpace(extra) ? marker : marker + " " + extra.Trim();
    }

    private static void Report(IProgress<DryRunProgress>? progress, string breadcrumbPath, TimeSpan elapsed)
    {
        if (progress is null)
            return;

        var trail = BreadcrumbFile.Read(breadcrumbPath);

        var message = trail.LoadingModule is { } loading
            ? Strings.Current.Format("Core.DryRun.Orchestrator.Progress.Loading", loading)
            : trail.Records.Count == 0
                ? Strings.Current["Core.DryRun.Orchestrator.Progress.Starting"]
                : Strings.Current["Core.DryRun.Orchestrator.Progress.Waiting"];

        progress.Report(new DryRunProgress(
            trail.CompletedCount, trail.SubModuleCount, trail.LoadingModule, elapsed, message));
    }

    private static DryRunVerdict Describe(
        string runId,
        DryRunStatus status,
        string failureMessage,
        DryRunResult? result,
        BreadcrumbTrail trail,
        LaunchConfigRestoreResult? configOutcome,
        TimeSpan elapsed,
        string resultPath,
        string breadcrumbPath,
        LaunchTargetKind? launchTarget,
        string companionLeftBehind)
    {
        var attribution = result?.Attribution ?? trail.Attribution;
        var attributionDegraded = result?.IsDegraded ?? trail.IsDegraded;
        var degradedReason = result is not null && result.DegradedReason.Length > 0
            ? result.DegradedReason
            : trail.DegradedReason;
        var subModuleCount = result?.SubModuleCount ?? trail.SubModuleCount;
        var notProtected = configOutcome?.NotProtected ?? [];

        var (finalStatus, summary) = Verdict(status, failureMessage, result, trail);

        if (attributionDegraded)
        {
            summary += " " + Strings.Current["Core.DryRun.Orchestrator.Summary.AttributionDegraded"]
                + (degradedReason.Length > 0 ? " " + degradedReason : string.Empty);
        }

        // A config BEM could not copy is a second, separate way for a run to be degraded, and it is
        // said in its own words: it is not a statement about how the failing module was attributed.
        if (notProtected.Count > 0)
        {
            var unprotected = Strings.Current.Plural(
                "Core.DryRun.Orchestrator.Summary.ConfigNotProtected",
                notProtected.Count,
                string.Join("; ", notProtected));

            summary += " " + unprotected;
            degradedReason = degradedReason.Length > 0 ? degradedReason + " " + unprotected : unprotected;
        }

        // Not folded into IsDegraded: that word is about how far the measurement can be trusted, and
        // this is about what BEM left on disk afterwards. It is said in its own sentence for that.
        if (companionLeftBehind.Length > 0)
        {
            summary += " " + Strings.Current.Format(
                "Core.DryRun.Orchestrator.Summary.CompanionLeftBehind", companionLeftBehind);
        }

        var degraded = attributionDegraded || notProtected.Count > 0;

        var failedFromResult = result?.FailedModules ?? [];

        var failed = failedFromResult.Count > 0 ? failedFromResult : trail.Failures;

        return new DryRunVerdict(
            finalStatus,
            summary,
            attribution,
            degraded,
            degradedReason,
            subModuleCount,
            failed,
            finalStatus == DryRunStatus.Booted ? null : trail.LoadingModule,
            elapsed,
            configOutcome?.ChangedPaths ?? [],
            result,
            trail,
            runId,
            File.Exists(resultPath) ? resultPath : null,
            File.Exists(breadcrumbPath) ? breadcrumbPath : null,
            launchTarget,
            configOutcome,
            companionLeftBehind);
    }

    private static (DryRunStatus, string) Verdict(
        DryRunStatus status, string failureMessage, DryRunResult? result, BreadcrumbTrail trail)
    {
        if (status == DryRunStatus.Failed)
            return (status, Strings.Current.Format("Core.DryRun.Orchestrator.Verdict.Failed", failureMessage));

        if (status is DryRunStatus.Canceled)
            return (status, Stopped(Strings.Current["Core.DryRun.Orchestrator.Verdict.Canceled"], trail));

        if (status is DryRunStatus.TimedOut)
            return (status, Stopped(Strings.Current["Core.DryRun.Orchestrator.Verdict.TimedOut"], trail));

        if (result is not null && result.Booted)
        {
            return (DryRunStatus.Booted,
                Strings.Current.Plural(
                    "Core.DryRun.Orchestrator.Verdict.Booted", result.SubModuleCount, result.PatchedOverrides));
        }

        if (result is not null)
        {
            var names = string.Join(", ", result.FailedModules.Select(m => m.Label));

            return (DryRunStatus.ModuleThrew,
                Strings.Current.Plural("Core.DryRun.Orchestrator.Verdict.ModuleThrew", result.FailedModules.Count, names));
        }

        // No result file means the game died before the first tick, which is exactly the run a dry
        // run exists to catch. The breadcrumbs are then the whole record, and a throw in them is a
        // caught exception the companion watched happen, not a guess from where the trail ends.
        if (trail.Failures.Count > 0)
        {
            return (DryRunStatus.ModuleThrew,
                Strings.Current.Plural(
                    "Core.DryRun.Orchestrator.Verdict.ThrewDuringLoad",
                    trail.CompletedCount - trail.Failures.Count,
                    Threw(trail.Failures)));
        }

        if (trail.LoadingModule is not null || trail.Records.Count > 0)
        {
            return (DryRunStatus.StoppedDuringLoad,
                Stopped(Strings.Current["Core.DryRun.Orchestrator.Verdict.StoppedDuringLoad"], trail));
        }

        // Scoped to the companion on purpose. This used to read "nothing was measured", which was taken
        // for a statement about the whole run and was not one: the run's own logs, the game's log and
        // any crash report were being captured at the same time and are read separately.
        return (DryRunStatus.NoCapture, Strings.Current["Core.DryRun.Orchestrator.Verdict.NoCapture"]);
    }

    private static string Threw(IReadOnlyList<DryRunModuleOutcome> failures) => string.Join(" ", failures.Select(
        failure => failure.ExceptionType is { Length: > 0 } type
            ? Strings.Current.Format(
                "Core.DryRun.Orchestrator.Threw.WithType", failure.Label, type, Where(failure), failure.ExceptionMessage)
            : Strings.Current.Format("Core.DryRun.Orchestrator.Threw.NoType", failure.Label, Where(failure))));

    private static string Where(DryRunModuleOutcome failure) =>
        failure.TypeName.Length > 0 ? failure.TypeName + ".OnSubModuleLoad" : "its own OnSubModuleLoad";

    private static string Stopped(string prefix, BreadcrumbTrail trail)
    {
        if (trail.LoadingModule is { } loading)
            return Strings.Current.Format("Core.DryRun.Orchestrator.Stopped.WhileLoading", prefix, loading);

        if (trail.LastPhase.Length > 0)
            return Strings.Current.Format("Core.DryRun.Orchestrator.Stopped.DuringPhase", prefix, trail.LastPhase);

        return Strings.Current.Plural("Core.DryRun.Orchestrator.Stopped.NoBreadcrumb", trail.CompletedCount, prefix);
    }
}
