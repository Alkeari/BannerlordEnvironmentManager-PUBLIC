using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.LoadOrder;

namespace BannerlordEnvironmentManager.Core.DryRun;

// Runs one experiment and says what happened. In automatic mode this is a dry run; in guided mode it
// writes the load order, waits for the user to play to the point the crash happens, and reads their
// answer.
public delegate Task<BisectionOutcome> BisectionOracle(
    BisectionExperiment experiment,
    CancellationToken cancellationToken);

// Wraps every experiment the way a dry run wraps itself: LauncherData.xml and the whole Configs tree
// are copied before it and put back afterwards, unconditionally, in a finally block. A bisect that
// ate the user's load order would be far worse than no bisect, and a guided one writes that file on
// purpose, so this is not optional.
public sealed class BisectionRunner(
    string snapshotRoot,
    string launcherDataPath,
    string configsFolderPath,
    LoadOrderBackupStore? backups = null)
{
    private readonly List<LaunchConfigRestoreResult> _configOutcomes = [];

    private readonly List<BisectionExperiment> _recalled = [];

    // One entry per experiment. Discarding these was how a bisect could change a config file, or
    // fail to protect one at all, and have nothing to say about either.
    public IReadOnlyList<LaunchConfigRestoreResult> ConfigOutcomes => _configOutcomes;

    // The runs BEM did not have to make, because the user had already made them and said so. Reported
    // rather than quietly absorbed: a search that answered four of its own questions from a record is a
    // different thing to read than one that spent four launches.
    public IReadOnlyList<BisectionExperiment> Recalled => _recalled;

    public async Task<BisectionConclusion> RunAsync(
        BisectionSession session,
        BisectionOracle oracle,
        IProgress<BisectionExperiment>? progress = null,
        BisectionStore? store = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(oracle);

        TakeSafetyBackup(session);

        while (session.Next() is { } experiment)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A configuration the user has already run, matched module for module. Nothing is written
            // to disk, nothing is launched, and the search moves on an answer it already had. Checked
            // before the snapshot so a recalled run costs neither a launch nor a copy of Configs.
            if (session.Recall(experiment) is { } recalled)
            {
                _recalled.Add(experiment);
                session.Record(recalled, fromRecord: true);
                store?.Save(session.Save());
                continue;
            }

            progress?.Report(experiment);

            LaunchConfigSnapshot? snapshot = null;
            var outcome = BisectionOutcome.Invalid;

            // Written before the configuration reaches disk, so a BEM that never comes back from
            // this experiment leaves a record saying so and naming the order to put back.
            session.MarkExperimentInFlight(true);
            store?.Save(session.Save());

            try
            {
                snapshot = LaunchConfigSnapshot.Capture(
                    launcherDataPath,
                    configsFolderPath,
                    Path.Combine(snapshotRoot, session.Id, $"{experiment.Number}-{experiment.Attempt}"));

                outcome = await oracle(experiment, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A run that could not happen says nothing about the configuration it was going to
                // test. Recording it as clean is what corrupts a search.
                outcome = BisectionOutcome.Invalid;
            }
            finally
            {
                if (snapshot is not null)
                {
                    _configOutcomes.Add(snapshot.Restore());
                    snapshot.Dispose();
                }

                // Cleared only after the restore, because between the two the user's own load order
                // is not on disk yet and that is exactly the window worth recovering from.
                session.MarkExperimentInFlight(false);
                store?.Save(session.Save());
            }

            cancellationToken.ThrowIfCancellationRequested();

            session.Record(outcome);
            store?.Save(session.Save());
        }

        return session.Conclusion;
    }

    private void TakeSafetyBackup(BisectionSession session)
    {
        if (backups is null || session.SafetyBackupPath.Length > 0)
            return;

        try
        {
            if (backups.Capture(launcherDataPath, "before bisection") is { } safety)
                session.RecordSafetyBackup(safety.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A search without a durable backup is worse than one with it, but refusing to search at
            // all is worse again: every experiment is still wrapped in its own snapshot.
        }
    }
}

// The automatic path: a load-time crash reproduces or does not inside a dry run, so nobody has to be
// in the loop. A run of 60 to 120 seconds means eight of them is about a quarter of an hour.
public static class DryRunBisection
{
    public static BisectionOracle Oracle(
        DryRunOrchestrator orchestrator,
        DryRunRequest template,
        IReadOnlyList<ModuleEntry> loadOrder,
        IProgress<DryRunProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(loadOrder);

        var byId = loadOrder.ToDictionary(e => e.Id);

        return async (experiment, cancellationToken) =>
        {
            var modules = experiment.Enabled
                .Where(byId.ContainsKey)
                .Select(id => byId[id] with { IsEnabled = true })
                .ToList();

            var verdict = await orchestrator
                .RunAsync(template with { EnabledModules = modules }, progress, cancellationToken)
                .ConfigureAwait(false);

            return Read(verdict);
        };
    }

    // Anything that is not a clean boot or a named failure is no information. A refused run, a
    // timeout or a run that captured nothing says something about BEM, not about the module set.
    public static BisectionOutcome Read(DryRunVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return verdict.Status switch
        {
            DryRunStatus.ModuleThrew or DryRunStatus.StoppedDuringLoad => BisectionOutcome.Reproduced,
            DryRunStatus.Booted => BisectionOutcome.NotReproduced,
            _ => BisectionOutcome.Invalid
        };
    }
}

// What makes two crashes the same crash: the innermost exception's type and the frame it threw in.
// The wrapper is deliberately not part of it, because a TargetInvocationException wrapping the same
// fault is the same crash.
public sealed record CrashSignature(string ThrownTypeFullName, string FaultFrame)
{
    public static CrashSignature Of(CrashReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new CrashSignature(
            report.Root?.TypeFullName ?? string.Empty,
            report.FaultFrame?.QualifiedName ?? string.Empty);
    }

    public bool Matches(CrashReport other)
    {
        if (other is null)
            return false;

        var candidate = Of(other);

        return string.Equals(ThrownTypeFullName, candidate.ThrownTypeFullName, StringComparison.Ordinal)
            && string.Equals(FaultFrame, candidate.FaultFrame, StringComparison.Ordinal);
    }
}

// The user plays; BEM watches the crash folders and decides when it can. It only asks when it
// genuinely cannot tell, because "nothing appeared" and "it did not crash" are different statements:
// the user may simply not have reached the point that crashes.
public sealed class BisectionOutcomeDetector(
    IReadOnlyList<CrashReportFolder> folders,
    CrashSignature signature)
{
    public string Question => Strings.Current.Format(
        "Core.DryRun.Bisection.OutcomeDetector.Question", Simple(signature.ThrownTypeFullName));

    public BisectionOutcome? Detect(DateTime startedUtc)
    {
        var search = CrashReportLocator.Find(folders);

        foreach (var report in search.Reports)
        {
            if (report.Written.UtcDateTime < startedUtc)
                continue;

            if (signature.Matches(report.Report))
                return BisectionOutcome.Reproduced;
        }

        return null;
    }

    private static string Simple(string typeFullName)
    {
        var lastDot = typeFullName.LastIndexOf('.');

        return lastDot < 0 ? typeFullName : typeFullName[(lastDot + 1)..];
    }
}
