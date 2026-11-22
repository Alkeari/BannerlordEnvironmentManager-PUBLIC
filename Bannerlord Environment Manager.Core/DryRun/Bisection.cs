using System.Globalization;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.DryRun;

public enum BisectionMode
{
    // Load-time and boot-to-menu failures, driven over dry runs with nobody in the loop.
    Automatic,

    // Anything that needs gameplay to reproduce. BEM configures each experiment, the user plays to
    // the point the crash happens and reports what did or did not happen, BEM picks the next set.
    Guided
}

public enum BisectionOutcome
{
    Reproduced,
    NotReproduced,

    // The run told us nothing: it failed to start, was abandoned, or crashed differently. Counted as
    // no information rather than as a clean run, because a false clean sends the search into the
    // wrong half and it never recovers.
    Invalid
}

public enum BisectionResult
{
    InProgress,
    Caused,
    NoModuleResponsible,
    RefusedNondeterministic,
    Inconclusive,

    // Scoped searches only. The crash still happened with every module in the scope turned off, so the
    // scope is the wrong set. This is a different statement to NoModuleResponsible: the modules outside
    // the scope were never turned off, so nothing about them was tested and none of them is cleared.
    NotInScope,

    // A scope was given and nothing in it is a module a search could have turned off.
    RefusedEmptyScope
}

public enum BisectionPhase
{
    MeasuringReproduction,
    ControlMinimal,
    TestingRanked,
    Narrowing,
    Confirming,
    Finished
}

// A run the user has already made, and what it did. The set named here is the part of the candidate
// pool that was ON for that run: everything else in the pool was off, and everything outside the pool
// stayed on. That is the exact shape of every configuration a search proposes, which is what lets an
// observation stand in for a launch with no inference done on it at all.
public sealed record BisectionObservation(IReadOnlyList<ModuleId> Enabled, bool Reproduced)
{
    public string Describe()
    {
        var ending = Reproduced
            ? Strings.Current["Core.DryRun.Bisection.Observation.Ending.Crashed"]
            : Strings.Current["Core.DryRun.Bisection.Observation.Ending.NotCrashed"];

        return Enabled.Count == 0
            ? Strings.Current.Format("Core.DryRun.Bisection.Observation.AllOff", ending)
            : Strings.Current.Format(
                "Core.DryRun.Bisection.Observation.SomeOn", string.Join(", ", Enabled.Select(m => m.Value)), ending);
    }
}

public sealed record BisectionRequest(
    IReadOnlyList<ModuleEntry> LoadOrder,
    IReadOnlyList<ModuleId> RankedSuspects,
    BisectionMode Mode,
    string ReproductionStep = "",
    int ControlAttempts = 3,
    // The set the search is confined to. Null means no scope was asked for and the search runs over
    // the whole non-pinned load order, exactly as it always has. An empty list is not the same thing:
    // it is a scope that named nothing, and it is refused rather than quietly widened.
    IReadOnlyList<ModuleId>? Scope = null,
    IReadOnlyList<BisectionObservation>? KnownOutcomes = null);

public sealed record BisectionExperiment(
    int Number,
    int Attempt,
    int EstimatedRemaining,
    bool IsControl,
    IReadOnlyList<ModuleId> Enabled,
    IReadOnlyList<ModuleId> Disabled,
    IReadOnlyList<ModuleId> UnderTest,
    string Purpose,
    string Instruction)
{
    public string Describe() =>
        Strings.Current.Format("Core.DryRun.Bisection.Experiment.RunOf", Number, Number + EstimatedRemaining - 1)
        + " " + Strings.Current.Plural("Core.DryRun.Bisection.Experiment.Enabled", Enabled.Count)
        + ", " + Strings.Current.Format("Core.DryRun.Bisection.Experiment.Disabled", Disabled.Count)
        + " " + Purpose;

    // The set that reaches the game, as opposed to the set written to LauncherData.xml. A direct
    // launch carries its module list on the command line, where there is no way to spell "off": every
    // id on it loads. Handing that list the whole load order with flags on it launches everything.
    public IReadOnlyList<ModuleEntry> ModulesToLaunch(IReadOnlyList<ModuleEntry> loadOrder)
    {
        ArgumentNullException.ThrowIfNull(loadOrder);

        return
        [
            .. loadOrder
                .Where(e => Enabled.Contains(e.Id) && !e.IsOrphan)
                .Select(e => e with { IsEnabled = true })
        ];
    }
}

// One run that actually happened, and what it did. The engine used to keep only its own running state,
// which is enough to pick the next configuration and not enough to say afterwards how anything was
// established: a finished session could name a culprit and had no record of the launches that named it.
// Nothing here is collected that the search did not already produce; it is the same experiment and the
// same answer, kept instead of discarded.
public sealed record BisectionRunRecord(
    int Number,
    bool IsControl,
    IReadOnlyList<ModuleId> Enabled,
    IReadOnlyList<ModuleId> Disabled,
    IReadOnlyList<ModuleId> UnderTest,
    BisectionOutcome Outcome,
    // True when the search answered this configuration from a run the user had already made and
    // recorded, rather than spending a launch on it. Still a real launch, just an earlier one.
    bool FromRecord)
{
    public bool Reproduced => Outcome is BisectionOutcome.Reproduced;
}

public sealed record BisectionRunSnapshot(
    int Number,
    bool IsControl,
    IReadOnlyList<string> Enabled,
    IReadOnlyList<string> Disabled,
    IReadOnlyList<string> UnderTest,
    string Outcome,
    bool FromRecord);

// The only place in BEM allowed the word "caused". Everything the attribution engine produces says
// suspect, because it ranks hypotheses from evidence that was already on disk. This is the layer
// that runs the experiment and watches the crash come and go, which is what earns the word.
public sealed record BisectionConclusion(
    BisectionResult Result,
    IReadOnlyList<ModuleId> Cause,
    string Headline,
    IReadOnlyList<string> Detail,
    double ReproductionRate,
    int Experiments);

public sealed record BisectionSnapshot(
    string Id,
    string Phase,
    int ControlAttempts,
    int ControlReproduced,
    int CleanRunsRequired,
    int Settled,
    int ConsecutiveClean,
    IReadOnlyList<string> Failing,
    IReadOnlyList<string> Cleared,
    IReadOnlyList<string> Ranked,
    int Granularity,
    int SubsetIndex,
    bool TestingComplements,
    int RankedStep,
    string Result,
    DateTime StartedUtc,
    DateTime UpdatedUtc,
    // The load order backup taken before the search touched anything, and whether an experiment was
    // in flight when this was last written. A guided run needs the user to leave BEM and play, so
    // BEM being closed or crashing between experiments is ordinary. Together these are what lets the
    // next cold start find a stranded load order and offer to put it back.
    string SafetyBackupPath = "",
    bool ExperimentInFlight = false,
    // A scoped search has to resume scoped. Without these two on the snapshot, picking a search up
    // again would widen it back to the whole load order and re-ask everything already answered, and
    // it would do it silently. Null Scope means the search was never scoped; an empty list would mean
    // a scope that resolved to nothing, which is a refusal rather than a search.
    IReadOnlyList<string>? Scope = null,
    IReadOnlyList<BisectionObservationSnapshot>? Observations = null,
    // Every run the search has settled, in the order it made them. This is the only durable record of
    // how a conclusion was reached, and a guided search spans days, so it has to survive BEM closing
    // along with everything else the session carries.
    IReadOnlyList<BisectionRunSnapshot>? Runs = null,
    // The user's own words for what they do to make the crash happen. Persisted so a session picked up
    // later, or read long after it finished, still says what was being reproduced.
    string ReproductionStep = "");

public sealed record BisectionObservationSnapshot(IReadOnlyList<string> Enabled, bool Reproduced);

// Delta debugging (ddmin), not a halving search.
//
// A halving search assumes one faulty element. Mod crashes are frequently an interaction: A alone is
// fine, B alone is fine, A and B together crash. On that shape a halving search sees both halves
// test clean and has nowhere to go. ddmin splits into n partitions and tests both each partition and
// each complement, doubling n when neither reproduces, so it converges on a 1-minimal failing SET
// rather than on a single element that may not exist.
public sealed class BisectionSession
{
    private const double MinimumReproductionRate = 2d / 3d;

    private readonly BisectionRequest _request;

    private readonly List<ModuleId> _order;

    private readonly HashSet<ModuleId> _pinned;

    private readonly List<ModuleId> _cleared = [];

    private readonly List<ModuleId> _ranked = [];

    private readonly List<ModuleId> _scope = [];

    private readonly List<ModuleId> _outsideScope = [];

    private readonly List<ModuleId> _pool;

    private readonly List<BisectionObservation> _observations = [];

    private readonly List<BisectionRunRecord> _runs = [];

    private readonly bool _scoped;

    private List<ModuleId> _failing = [];

    private BisectionExperiment? _pending;

    private BisectionPhase _phase = BisectionPhase.MeasuringReproduction;

    private BisectionResult _result = BisectionResult.InProgress;

    private int _controlAttempts;

    private int _controlReproduced;

    private int _cleanRunsRequired = 1;

    private int _consecutiveClean;

    private int _settled;

    private int _granularity = 2;

    private int _subsetIndex;

    private bool _testingComplements;

    private int _rankedStep;

    private BisectionSession(BisectionRequest request)
    {
        _request = request;
        _order = [.. request.LoadOrder.Select(e => e.Id)];
        _pinned = Pinned(request.LoadOrder);

        var candidates = _order.Where(id => !_pinned.Contains(id)).ToList();

        _scoped = request.Scope is not null;

        // A scope is the user saying they already know which modules the crash is inside, so those are
        // the only ones the search may turn off. Only a module the search could have turned off anyway
        // can be in it: a pinned module named in a scope is dropped rather than honored, because no
        // experiment is able to turn it off and keeping it would make the count on screen a lie.
        if (_scoped)
            _scope.AddRange(candidates.Where(id => request.Scope!.Contains(id)).Distinct());

        _pool = _scoped ? _scope : candidates;

        if (_scoped)
            _outsideScope.AddRange(candidates.Where(id => !_scope.Contains(id)));

        // The ranked suspects lead, so the first partition ddmin tests is the one the evidence
        // already points at rather than whichever half the list happened to split into.
        _ranked = [.. request.RankedSuspects.Where(_pool.Contains).Distinct()];
        _failing = [.. _ranked, .. _pool.Where(id => !_ranked.Contains(id))];

        // An observation names a configuration of the pool. One that names a module outside the pool
        // describes a different question, and trimming it to fit would silently change what the user
        // recorded, so it is dropped whole rather than repaired.
        foreach (var observation in request.KnownOutcomes ?? [])
        {
            if (observation.Enabled.All(_pool.Contains))
                _observations.Add(observation);
        }

        StartedUtc = DateTime.UtcNow;
        Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" +
            Guid.NewGuid().ToString("N")[..4];

        // A scope nothing resolved in leaves no experiment that means anything. The control-minimal
        // run would be the full load order under another name, it would reproduce, and it would be
        // read as no mod being responsible. Refusing here is the difference between saying nothing
        // and saying something wrong.
        if (_scoped && _scope.Count == 0)
        {
            _result = BisectionResult.RefusedEmptyScope;
            _phase = BisectionPhase.Finished;
        }
    }

    public string Id { get; private set; }

    public DateTime StartedUtc { get; private set; }

    public string SafetyBackupPath { get; private set; } = string.Empty;

    public bool ExperimentInFlight { get; private set; }

    public BisectionPhase Phase => _phase;

    public bool IsScoped => _scoped;

    // The modules the search may turn off, and the ones it never will. Both are on screen before the
    // first run, because a scope entered wrong costs the user a launch per mistake.
    public IReadOnlyList<ModuleId> Scope => _scope;

    public IReadOnlyList<ModuleId> OutsideScope => _outsideScope;

    public IReadOnlyList<BisectionObservation> KnownOutcomes => _observations;

    // Every run this search has settled, oldest first. What a conclusion was built out of, rather than
    // only the conclusion.
    public IReadOnlyList<BisectionRunRecord> Runs => _runs;

    public string ReproductionStep => _request.ReproductionStep;

    public int CleanRunsRequired => _cleanRunsRequired;

    public double ReproductionRate =>
        _controlAttempts == 0 ? 0 : (double)_controlReproduced / _controlAttempts;

    public static BisectionSession Start(BisectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new BisectionSession(request);
    }

    public static BisectionSession? Resume(BisectionSnapshot? snapshot, BisectionRequest request)
    {
        if (snapshot is null)
            return null;

        ArgumentNullException.ThrowIfNull(request);

        // The scope and the recorded outcomes come off the snapshot, never off the request. A caller
        // that rebuilt the request from the environment as it is now and forgot either one would
        // resume a scoped search over the whole load order and re-ask what was already answered, and
        // would do it without saying so. Putting it here means no caller can get it wrong.
        var effective = request with
        {
            Scope = snapshot.Scope is null ? null : [.. snapshot.Scope.Select(id => new ModuleId(id))],
            ReproductionStep = string.IsNullOrWhiteSpace(request.ReproductionStep)
                ? snapshot.ReproductionStep
                : request.ReproductionStep,
            KnownOutcomes =
            [
                .. (snapshot.Observations ?? []).Select(observation => new BisectionObservation(
                    [.. observation.Enabled.Select(id => new ModuleId(id))],
                    observation.Reproduced))
            ]
        };

        var session = new BisectionSession(effective);

        // A scope that no longer resolves against the load order on disk cannot be resumed into.
        // Restoring the saved state over the refusal would resume a search with nothing to search.
        if (session._result is BisectionResult.RefusedEmptyScope)
            return session;

        session.Id = snapshot.Id;
        session.StartedUtc = snapshot.StartedUtc;
        session._phase = Enum.TryParse<BisectionPhase>(snapshot.Phase, out var phase)
            ? phase
            : BisectionPhase.MeasuringReproduction;
        session._result = Enum.TryParse<BisectionResult>(snapshot.Result, out var result)
            ? result
            : BisectionResult.InProgress;
        session._controlAttempts = snapshot.ControlAttempts;
        session._controlReproduced = snapshot.ControlReproduced;
        session._cleanRunsRequired = Math.Max(1, snapshot.CleanRunsRequired);
        session._settled = snapshot.Settled;
        session._consecutiveClean = snapshot.ConsecutiveClean;
        session._granularity = Math.Max(2, snapshot.Granularity);
        session._subsetIndex = snapshot.SubsetIndex;
        session._testingComplements = snapshot.TestingComplements;
        session._rankedStep = snapshot.RankedStep;
        session.SafetyBackupPath = snapshot.SafetyBackupPath;
        session.ExperimentInFlight = snapshot.ExperimentInFlight;

        session._failing = [.. snapshot.Failing.Select(id => new ModuleId(id))];
        session._cleared.Clear();
        session._cleared.AddRange(snapshot.Cleared.Select(id => new ModuleId(id)));
        session._ranked.Clear();
        session._ranked.AddRange(snapshot.Ranked.Select(id => new ModuleId(id)));

        session._runs.Clear();
        session._runs.AddRange((snapshot.Runs ?? []).Select(run => new BisectionRunRecord(
            run.Number,
            run.IsControl,
            [.. run.Enabled.Select(id => new ModuleId(id))],
            [.. run.Disabled.Select(id => new ModuleId(id))],
            [.. run.UnderTest.Select(id => new ModuleId(id))],
            Enum.TryParse<BisectionOutcome>(run.Outcome, out var outcome) ? outcome : BisectionOutcome.Invalid,
            run.FromRecord)));

        return session;
    }

    public BisectionSnapshot Save() => new(
        Id,
        _phase.ToString(),
        _controlAttempts,
        _controlReproduced,
        _cleanRunsRequired,
        _settled,
        _consecutiveClean,
        [.. _failing.Select(m => m.Value)],
        [.. _cleared.Select(m => m.Value)],
        [.. _ranked.Select(m => m.Value)],
        _granularity,
        _subsetIndex,
        _testingComplements,
        _rankedStep,
        _result.ToString(),
        StartedUtc,
        DateTime.UtcNow,
        SafetyBackupPath,
        ExperimentInFlight,
        _scoped ? [.. _scope.Select(m => m.Value)] : null,
        [
            .. _observations.Select(observation => new BisectionObservationSnapshot(
                [.. observation.Enabled.Select(m => m.Value)],
                observation.Reproduced))
        ],
        [
            .. _runs.Select(run => new BisectionRunSnapshot(
                run.Number,
                run.IsControl,
                [.. run.Enabled.Select(m => m.Value)],
                [.. run.Disabled.Select(m => m.Value)],
                [.. run.UnderTest.Select(m => m.Value)],
                run.Outcome.ToString(),
                run.FromRecord))
        ],
        _request.ReproductionStep);

    // Answers an experiment from a run the user has already made, or says it cannot. Nothing here
    // infers anything: the configuration has to match module for module.
    //
    // It matches against the set the experiment would actually run rather than against the set it
    // nominally has under test, because those are not the same thing. A module the search has already
    // cleared is enabled in every later run without being under test, so keying on UnderTest would
    // match a record describing a strictly smaller configuration than the one about to run. That is
    // exactly the false answer the file's own warnings are about.
    public BisectionOutcome? Recall(BisectionExperiment experiment)
    {
        ArgumentNullException.ThrowIfNull(experiment);

        if (_observations.Count == 0)
            return null;

        // A control run is the measurement of how reliably the crash happens, and that is what decides
        // whether the search may be trusted at all. A record cannot measure a rate, and standing one in
        // for a control would report a certainty nobody established.
        if (experiment.IsControl)
            return null;

        var actual = experiment.Enabled.Where(_pool.Contains).ToList();
        var matches = _observations.Where(o => SameSet(o.Enabled, actual)).ToList();

        if (matches.Count == 0)
            return null;

        // Two records disagreeing about the same configuration is not a tie to break. One of them is
        // wrong, and a wrong answer here sends ddmin into a half the fault is not in and it never
        // recovers, so BEM spends the launch instead.
        if (matches.Any(match => match.Reproduced != matches[0].Reproduced))
            return null;

        return matches[0].Reproduced ? BisectionOutcome.Reproduced : BisectionOutcome.NotReproduced;
    }

    private static bool SameSet(IReadOnlyList<ModuleId> left, IReadOnlyList<ModuleId> right) =>
        new HashSet<ModuleId>(left).SetEquals(right);

    // Written before the search touches anything, so a cold start after a crash has a load order to
    // offer back rather than only a record of what was being searched.
    public void RecordSafetyBackup(string path) => SafetyBackupPath = path;

    // Set while an experiment's configuration is on disk and the user is playing it. A session that
    // is still flagged when BEM next starts is one BEM did not live through.
    public void MarkExperimentInFlight(bool inFlight) => ExperimentInFlight = inFlight;

    public BisectionExperiment? Next()
    {
        if (_result is not BisectionResult.InProgress)
            return null;

        return _pending ??= Build();
    }

    public void Record(BisectionOutcome outcome, bool fromRecord = false)
    {
        if (_result is not BisectionResult.InProgress)
            return;

        _pending ??= Build();

        if (_pending is null)
            return;

        // Written before the Invalid guard below, because a run that told nobody anything is still a
        // run that happened and a report that silently drops it would show a shorter search than the
        // one the user sat through.
        _runs.Add(new BisectionRunRecord(
            _pending.Number,
            _pending.IsControl,
            _pending.Enabled,
            _pending.Disabled,
            _pending.UnderTest,
            outcome,
            fromRecord));

        // No information. The same configuration is offered again rather than counted as anything.
        if (outcome is BisectionOutcome.Invalid)
            return;

        _settled++;
        var reproduced = outcome is BisectionOutcome.Reproduced;

        if (_phase is BisectionPhase.MeasuringReproduction)
        {
            _controlAttempts++;

            if (reproduced)
                _controlReproduced++;

            _pending = null;

            if (_controlAttempts >= _request.ControlAttempts)
                SettleReproductionRate();

            return;
        }

        if (reproduced)
        {
            _consecutiveClean = 0;
            _pending = null;
            Advance(true);
            return;
        }

        _consecutiveClean++;

        // A flaky crash needs the node held clean several times over. One non-reproduction of a crash
        // that only shows two times in three says almost nothing.
        if (_consecutiveClean < _cleanRunsRequired)
            return;

        _consecutiveClean = 0;
        _pending = null;
        Advance(false);
    }

    public BisectionConclusion Conclusion => _result switch
    {
        BisectionResult.RefusedNondeterministic => new BisectionConclusion(
            _result,
            [],
            Strings.Current.Plural(
                "Core.DryRun.Bisection.Conclusion.RefusedNondeterministic.Headline", _controlReproduced, _controlAttempts),
            [
                Strings.Current["Core.DryRun.Bisection.Conclusion.RefusedNondeterministic.Detail1"],
                Strings.Current["Core.DryRun.Bisection.Conclusion.RefusedNondeterministic.Detail2"]
            ],
            ReproductionRate,
            _settled),

        BisectionResult.NoModuleResponsible => new BisectionConclusion(
            _result,
            [],
            Strings.Current["Core.DryRun.Bisection.Conclusion.NoModuleResponsible.Headline"],
            [Strings.Current["Core.DryRun.Bisection.Conclusion.NoModuleResponsible.Detail"]],
            ReproductionRate,
            _settled),

        BisectionResult.NotInScope => new BisectionConclusion(
            _result,
            [],
            Strings.Current.Plural("Core.DryRun.Bisection.Conclusion.NotInScope.Headline", _scope.Count),
            [
                Strings.Current.Plural("Core.DryRun.Bisection.Conclusion.NotInScope.Detail1", _outsideScope.Count),
                Strings.Current["Core.DryRun.Bisection.Conclusion.NotInScope.Detail2"]
            ],
            ReproductionRate,
            _settled),

        BisectionResult.RefusedEmptyScope => new BisectionConclusion(
            _result,
            [],
            Strings.Current["Core.DryRun.Bisection.Conclusion.RefusedEmptyScope.Headline"],
            [
                Strings.Current["Core.DryRun.Bisection.Conclusion.RefusedEmptyScope.Detail1"],
                Strings.Current["Core.DryRun.Bisection.Conclusion.RefusedEmptyScope.Detail2"]
            ],
            ReproductionRate,
            _settled),

        BisectionResult.Caused => Caused(),

        BisectionResult.Inconclusive => new BisectionConclusion(
            _result,
            [],
            Strings.Current["Core.DryRun.Bisection.Conclusion.Inconclusive.Headline"],
            [Strings.Current["Core.DryRun.Bisection.Conclusion.Inconclusive.Detail"]],
            ReproductionRate,
            _settled),

        _ => new BisectionConclusion(
            _result,
            [],
            Strings.Current.Plural("Core.DryRun.Bisection.Conclusion.InProgress.Headline", _settled),
            [Describe()],
            ReproductionRate,
            _settled)
    };

    private BisectionConclusion Caused()
    {
        var names = string.Join(" and ", _failing.Select(m => m.Value));

        var headline = _failing.Count == 1
            ? Strings.Current.Format("Core.DryRun.Bisection.Conclusion.Caused.HeadlineSingle", names)
            : Strings.Current.Format("Core.DryRun.Bisection.Conclusion.Caused.HeadlineMultiple", names);

        var reproducedTail = Strings.Current.Plural("Core.DryRun.Bisection.Conclusion.Caused.Reproduced", _controlReproduced)
            + Strings.Current.Plural("Core.DryRun.Bisection.Conclusion.Caused.InControlRuns", _controlAttempts)
            + (_scoped
                ? Strings.Current.Plural("Core.DryRun.Bisection.Conclusion.Caused.ScopedTail", _scope.Count)
                : Strings.Current["Core.DryRun.Bisection.Conclusion.Caused.UnscopedTail"]);

        return new BisectionConclusion(
            _result,
            [.. _failing],
            headline,
            [
                reproducedTail,
                _failing.Count == 1
                    ? Strings.Current.Format("Core.DryRun.Bisection.Conclusion.Caused.DisableSingle", names)
                    : Strings.Current.Format("Core.DryRun.Bisection.Conclusion.Caused.DisableMultiple", _failing.Count)
            ],
            ReproductionRate,
            _settled);
    }

    private void SettleReproductionRate()
    {
        if (ReproductionRate < MinimumReproductionRate)
        {
            _result = BisectionResult.RefusedNondeterministic;
            _phase = BisectionPhase.Finished;
            return;
        }

        _cleanRunsRequired = ReproductionRate >= 1 ? 1 : 3;
        _phase = BisectionPhase.ControlMinimal;
    }

    private void Advance(bool reproduced)
    {
        switch (_phase)
        {
            case BisectionPhase.ControlMinimal:
                if (reproduced)
                {
                    // Two different statements, and reporting the unscoped one for a scoped run would
                    // be a lie. With every mod off, the crash surviving means no mod is responsible.
                    // With only the scope off, it means the scope is the wrong set: the mods outside
                    // it were never turned off, so none of them was tested and none is cleared.
                    _result = _scoped ? BisectionResult.NotInScope : BisectionResult.NoModuleResponsible;
                    _phase = BisectionPhase.Finished;
                    return;
                }

                _phase = _ranked.Count > 0 ? BisectionPhase.TestingRanked : BisectionPhase.Narrowing;
                return;

            case BisectionPhase.TestingRanked:
                AdvanceRanked(reproduced);
                return;

            case BisectionPhase.Narrowing:
                AdvanceNarrowing(reproduced);
                return;

            case BisectionPhase.Confirming:
                _result = reproduced ? BisectionResult.Inconclusive : BisectionResult.Caused;
                _phase = BisectionPhase.Finished;
                return;
        }
    }

    // The schedule the research sets out: never start blind when the evidence already produced a
    // ranking. Two runs at most, then hand whatever survived to ddmin.
    private void AdvanceRanked(bool reproduced)
    {
        if (reproduced)
        {
            // It still crashed without them, so they are not necessary. A module known not to matter
            // is free to stay enabled, which keeps every later run closer to the real load order.
            var tested = _rankedStep == 0 ? _ranked.Take(1).ToList() : [.. _ranked];

            foreach (var module in tested)
            {
                _cleared.Add(module);
                _failing.Remove(module);
                _ranked.Remove(module);
            }

            if (_rankedStep == 0 && _ranked.Count > 0)
            {
                _rankedStep = 1;
                return;
            }

            _phase = BisectionPhase.Narrowing;
            return;
        }

        // The crash needs something in the set that was disabled, so ddmin starts from the whole
        // candidate list with the suspects already at its head.
        _phase = BisectionPhase.Narrowing;
    }

    private void AdvanceNarrowing(bool reproduced)
    {
        var subsets = Partition(_failing, _granularity);

        if (reproduced)
        {
            if (!_testingComplements)
            {
                // A partition reproduced on its own, so everything outside it is irrelevant and the
                // search restarts at the coarsest granularity inside it.
                _failing = subsets[_subsetIndex];
                _granularity = 2;
            }
            else
            {
                // The complement reproduced, so this partition is not needed. Granularity drops by
                // one rather than resetting, because the remainder is already partly narrowed.
                _failing = [.. _failing.Where(m => !subsets[_subsetIndex].Contains(m))];
                _granularity = Math.Max(_granularity - 1, 2);
            }

            _granularity = Math.Min(_granularity, Math.Max(_failing.Count, 2));
            _subsetIndex = 0;
            _testingComplements = false;

            // A single element cannot be split further, so the search is over.
            if (_failing.Count <= 1)
                Settle();

            return;
        }

        _subsetIndex++;

        if (_subsetIndex < subsets.Count)
            return;

        _subsetIndex = 0;

        if (!_testingComplements)
        {
            _testingComplements = true;
            return;
        }

        _testingComplements = false;

        if (_granularity < _failing.Count)
        {
            _granularity = Math.Min(_granularity * 2, _failing.Count);
            return;
        }

        // Nothing reproduced at any partition or any complement, at the finest granularity there is.
        // The set is 1-minimal.
        Settle();
    }

    // Confirming is the other half of the claim, and the half that earns the word: the crash has to
    // go away when this set is disabled and everything else is put back.
    private void Settle()
    {
        if (_failing.Count == 0)
        {
            _result = BisectionResult.Inconclusive;
            _phase = BisectionPhase.Finished;
            return;
        }

        _phase = BisectionPhase.Confirming;
    }

    private BisectionExperiment? Build()
    {
        switch (_phase)
        {
            case BisectionPhase.MeasuringReproduction:
                return Experiment(
                    [.. _order.Where(id => !_pinned.Contains(id))],
                    true,
                    Strings.Current.Format(
                        "Core.DryRun.Bisection.Purpose.MeasuringReproduction",
                        _controlAttempts + 1,
                        _request.ControlAttempts));

            case BisectionPhase.ControlMinimal:
                // Unscoped, this asks whether a mod is involved at all by turning every mod off. Under
                // a scope that is the wrong question: it tests a configuration the user is not asking
                // about, and the answer it would get is one the scope already assumes. The scoped form
                // of the same guard is to turn the whole scope off and leave everything outside it on.
                // If the crash survives that, it is not inside the set the user chose, and every run
                // after it would be narrowing a set the fault is not in. Same purpose, same protection
                // against a wasted search, asked about the scope instead of about the mod list.
                return Experiment(
                    [],
                    false,
                    _scoped
                        ? Strings.Current.Plural("Core.DryRun.Bisection.Purpose.ControlMinimalScoped", _scope.Count)
                        : Strings.Current["Core.DryRun.Bisection.Purpose.ControlMinimalUnscoped"]);

            case BisectionPhase.TestingRanked:
                return BuildRanked();

            case BisectionPhase.Narrowing:
                return BuildNarrowing();

            case BisectionPhase.Confirming:
                return Experiment(
                    [.. _failing.Count == _order.Count ? [] : Everything().Where(m => !_failing.Contains(m))],
                    false,
                    Strings.Current.Format("Core.DryRun.Bisection.Purpose.Confirming", Names(_failing)));

            default:
                return null;
        }
    }

    private BisectionExperiment BuildRanked()
    {
        var off = _rankedStep == 0 ? _ranked.Take(1).ToList() : [.. _ranked];

        return Experiment(
            [.. Everything().Where(m => !off.Contains(m))],
            false,
            _rankedStep == 0
                ? Strings.Current.Format("Core.DryRun.Bisection.Purpose.RankedFirst", Names(off))
                : Strings.Current.Plural("Core.DryRun.Bisection.Purpose.RankedAll", off.Count, Names(off)));
    }

    private BisectionExperiment BuildNarrowing()
    {
        var subsets = Partition(_failing, _granularity);

        if (subsets.Count == 0)
            return Experiment([], false, Strings.Current["Core.DryRun.Bisection.Purpose.NothingLeft"]);

        var index = Math.Min(_subsetIndex, subsets.Count - 1);
        var subset = subsets[index];

        return _testingComplements
            ? Experiment(
                [.. _failing.Where(m => !subset.Contains(m))],
                false,
                Strings.Current.Format(
                    "Core.DryRun.Bisection.Purpose.TestComplement", Names(subset), _failing.Count - subset.Count))
            : Experiment(
                subset,
                false,
                Strings.Current.Format(
                    "Core.DryRun.Bisection.Purpose.Narrow", subset.Count, _failing.Count, Names(subset)));
    }

    // Everything the search is allowed to turn off: the whole non-pinned load order, or just the scope
    // when one was given.
    private IEnumerable<ModuleId> Everything() => _pool;

    private BisectionExperiment Experiment(IReadOnlyList<ModuleId> underTest, bool isControl, string purpose)
    {
        var wanted = new HashSet<ModuleId>(_pinned);

        // Outside the scope is not under investigation, so it stays on for the whole search in exactly
        // the way a cleared module does. That is what makes every experiment a question about the
        // scope rather than about the load order. Empty when no scope was given, which leaves the
        // unscoped configuration identical to what it has always been.
        foreach (var module in _outsideScope)
            wanted.Add(module);

        foreach (var module in _cleared)
            wanted.Add(module);

        foreach (var module in underTest)
            wanted.Add(module);

        var enabled = Close(wanted);
        var disabled = _order.Where(id => !enabled.Contains(id)).ToList();
        var described = purpose + ScopeNote(disabled);

        return new BisectionExperiment(
            _settled + 1,
            _consecutiveClean + 1,
            Math.Max(1, Remaining()),
            isControl,
            enabled,
            disabled,
            underTest,
            described,
            Instruct(described));
    }

    // Says what is staying on because it is outside the scope, and names anything outside the scope
    // that had to go off anyway. Close() will turn a module off when something it needs is off, and a
    // module the user believes is on but is not is a run they would read the wrong way round.
    private string ScopeNote(IReadOnlyList<ModuleId> disabled)
    {
        if (!_scoped)
            return string.Empty;

        var note = Strings.Current.Plural(
            "Core.DryRun.Bisection.ScopeNote.Base", _scope.Count, _outsideScope.Count);

        var forced = disabled.Where(_outsideScope.Contains).ToList();

        return forced.Count == 0
            ? note
            : note + Strings.Current.Format("Core.DryRun.Bisection.ScopeNote.Forced", Names(forced));
    }

    private string Instruct(string purpose) => _request.Mode is BisectionMode.Guided
        ? Strings.Current.Format("Core.DryRun.Bisection.Instruct.Guided", purpose, Step())
        : Strings.Current.Format("Core.DryRun.Bisection.Instruct.Automatic", purpose);

    private string Step() => string.IsNullOrWhiteSpace(_request.ReproductionStep)
        ? Strings.Current["Core.DryRun.Bisection.Instruct.DefaultStep"]
        : _request.ReproductionStep;

    private int Remaining()
    {
        if (_phase is BisectionPhase.MeasuringReproduction)
            return _request.ControlAttempts - _controlAttempts + 4;

        if (_phase is BisectionPhase.Confirming)
            return 1;

        var size = Math.Max(_failing.Count, 2);

        return (int)Math.Ceiling(Math.Log2(size)) * 2 + 1;
    }

    // A run that fails to load because something it needed was disabled is a false clean, and a false
    // clean is what sends a search into the wrong half. The set that was actually tested is what gets
    // reported, never the set BEM meant to test.
    private List<ModuleId> Close(HashSet<ModuleId> wanted)
    {
        var byId = _request.LoadOrder.ToDictionary(e => e.Id);
        var settled = false;

        while (!settled)
        {
            settled = true;

            foreach (var id in wanted.ToList())
            {
                if (!byId.TryGetValue(id, out var entry))
                    continue;

                foreach (var dependency in entry.Dependencies)
                {
                    if (dependency.IsOptional || dependency.IsIncompatible || dependency.TargetId == id)
                        continue;

                    // BLSE supplies these at runtime and they have no folder by design.
                    if (BlseFeatures.IsFeatureId(dependency.TargetId))
                        continue;

                    if (!byId.ContainsKey(dependency.TargetId) || wanted.Contains(dependency.TargetId))
                        continue;

                    wanted.Remove(id);
                    settled = false;
                    break;
                }
            }
        }

        return [.. _order.Where(wanted.Contains)];
    }

    private static HashSet<ModuleId> Pinned(IReadOnlyList<ModuleEntry> order)
    {
        var tiers = ModuleTierMap.For(order);
        var pinned = new HashSet<ModuleId>();

        for (var i = 0; i < order.Count; i++)
        {
            // Harmony, ButterLib, UIExtenderEx and MCM stay on throughout, which is what the
            // community does by hand, and the game's own modules cannot be turned off at all.
            if (tiers[i] is ModuleTier.CrashHandler or ModuleTier.Infrastructure or ModuleTier.Official)
                pinned.Add(order[i].Id);
        }

        return pinned;
    }

    private static List<List<ModuleId>> Partition(List<ModuleId> modules, int parts)
    {
        var count = Math.Min(Math.Max(parts, 1), Math.Max(modules.Count, 1));
        var subsets = new List<List<ModuleId>>();
        var taken = 0;

        for (var i = 0; i < count; i++)
        {
            var size = (modules.Count - taken) / (count - i);
            subsets.Add(modules.GetRange(taken, size));
            taken += size;
        }

        return [.. subsets.Where(s => s.Count > 0)];
    }

    private static string Names(IReadOnlyList<ModuleId> modules) =>
        modules.Count == 0
            ? Strings.Current["Core.DryRun.Bisection.Names.Nothing"]
            : modules.Count <= 6
                ? string.Join(", ", modules.Select(m => m.Value))
                : Strings.Current.Format(
                    "Core.DryRun.Bisection.Names.AndMore",
                    string.Join(", ", modules.Take(6).Select(m => m.Value)),
                    modules.Count - 6);

    private string Describe() => _phase switch
    {
        BisectionPhase.MeasuringReproduction => Strings.Current.Format(
            "Core.DryRun.Bisection.Status.MeasuringReproduction", _controlReproduced, _controlAttempts),
        BisectionPhase.ControlMinimal => _scoped
            ? Strings.Current.Plural("Core.DryRun.Bisection.Status.ControlMinimalScoped", _scope.Count)
            : Strings.Current["Core.DryRun.Bisection.Status.ControlMinimalUnscoped"],
        BisectionPhase.TestingRanked =>
            Strings.Current.Plural("Core.DryRun.Bisection.Status.TestingRanked", _ranked.Count),
        BisectionPhase.Narrowing => Strings.Current.Plural("Core.DryRun.Bisection.Status.Narrowing", _failing.Count),
        BisectionPhase.Confirming => Strings.Current.Format("Core.DryRun.Bisection.Status.Confirming", Names(_failing)),
        _ => Strings.Current["Core.DryRun.Bisection.Status.Finished"]
    };
}
