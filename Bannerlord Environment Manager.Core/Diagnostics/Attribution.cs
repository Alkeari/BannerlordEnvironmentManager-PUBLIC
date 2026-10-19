using System.Globalization;
using BannerlordEnvironmentManager.Core.DryRun;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Modules.KnownIssues;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// Ordered weakest to strongest so a comparison reads the way the bands do. Deliberately no numeric
// score: a percentage would be false precision manufactured from weights nobody calibrated.
public enum ConfidenceBand
{
    Cleared,
    Context,
    Weak,
    Moderate,
    Strong,
    Confirmed
}

// Declared in the order a patch collision must be reported: the earliest-running kind first.
public enum PatchKind
{
    Transpiler,
    Prefix,
    Finalizer,
    Postfix
}

public enum FrameOrigin
{
    Unknown,
    Bcl,
    Game,
    Module
}

// Inference may reach Named at most: it ranks hypotheses. The verdict vocabulary belongs to an
// experiment that survived a disproof attempt, and no rule here runs an experiment.
public enum VerdictKind
{
    Unknown,
    Suspected,
    Named
}

// How the capture worked out which module owns a patch. Assembly location is exact; the Harmony
// owner is an author-chosen string that frequently is not a module id, so a patch attributed only
// from it is weaker evidence and has to travel labeled as such.
public enum PatchAttribution
{
    AssemblyLocation,
    HarmonyOwner,
    Unattributed
}

public sealed record PatchRecord(
    string TargetTypeFullName,
    string TargetMethodName,
    ModuleId ModuleId,
    PatchKind Kind,
    string Owner = "",
    PatchAttribution AttributedBy = PatchAttribution.AssemblyLocation,
    string PatchMethod = "");

public sealed record PatchRegistry(
    IReadOnlyList<PatchRecord> Patches,
    IReadOnlyList<ModuleId> ModuleSet,
    string? GameVersion = null,
    DateTime CapturedUtc = default,
    string RunId = "",
    int PatchedMethodCount = 0)
{
    public IReadOnlyList<PatchRecord> PatchersOf(string typeFullName, string methodName) =>
    [
        .. Patches.Where(p =>
            string.Equals(p.TargetTypeFullName, typeFullName, StringComparison.Ordinal)
            && string.Equals(p.TargetMethodName, methodName, StringComparison.Ordinal))
    ];
}

public sealed record ResolvedFrame(
    CrashFrame Frame,
    ExceptionNode Node,
    FrameOrigin Origin,
    string ResolvedTypeFullName,
    ModuleId ModuleId)
{
    public bool IsRoot => Node.Kind is CrashNodeKind.Root;
}

public sealed record AttributedModule(
    ModuleId ModuleId,
    ConfidenceBand Band,
    string Rule,
    string Evidence,
    string Disproof,
    int Depth,
    PatchKind? Kind = null,
    bool FromRegistry = false);

public sealed record AttributionVerdict(
    VerdictKind Kind,
    string Headline,
    IReadOnlyList<string> Detail,
    string NextStep,
    ModuleId? Module = null)
{
    public IReadOnlyList<string> Lines => [Headline, .. Detail, NextStep];
}

// One module that survived every filter the narrowing applies. Position is its place on the command
// line the run was launched with, which is a fact anyone can check against their own load order.
//
// There is deliberately no rank, no score and no distance on this record. An earlier build ordered
// candidates by how close they sat to the last module observed writing; testing the module
// that ordering put first did not stop the crash. A logger can be behind execution by an unknown
// number of modules and a failure need not happen at a module boundary at all, so that number
// ordered nothing and putting it on a row invited it to be read as a ranking anyway.
public sealed record CandidateModule(
    ModuleId ModuleId,
    int Position,
    string Call,
    string Corroboration)
{
    public string Describe()
    {
        var place = Strings.Current.Format(
            "Core.Diagnostics.Attribution.CandidateModule.Place",
            ModuleId, Position.ToString(CultureInfo.InvariantCulture), Call);

        return Corroboration.Length == 0 ? place : place + " " + Corroboration;
    }
}

// A set of modules and the one reason they are grouped, in the shape a scoped search takes as its
// input. The reason travels with the ids because a set handed on without it becomes a list of names
// again the moment it is read anywhere else.
public sealed record CandidateSet(IReadOnlyList<ModuleId> Modules, string Reason)
{
    public string Ids => string.Join(", ", Modules.Select(module => module.Value));
}

// What three pieces of evidence the user already has on disk reduce a load order to, and every step
// of the reduction so the reduction can be checked rather than believed.
//
// The output of this is a set. It is not a ranked list with a most-likely member, because every
// attempt to produce one on this crash has named the wrong module: the head of a list reads as an
// answer no matter how it is captioned. A set with a containment proof is a complete answer, and
// getting from a set to one name takes an experiment rather than an ordering.
public sealed record CandidateNarrowing(
    string Signature,
    string Mechanism,
    int TotalModules,
    int HadInitialized,
    int NotObserved,
    int Callers,
    IReadOnlyList<CandidateModule> Candidates,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Contradicted,
    IReadOnlyList<string> Caveats,
    string BoundaryFact,
    string Necessity,
    string Containment,
    string Promotion,
    string Disproof)
{
    // The handoff. A scoped search needs the ids and the reason they belong together, and nothing
    // else on this record is an input to one.
    public CandidateSet Set => new(
        [.. Candidates.Select(candidate => candidate.ModuleId)],
        Strings.Current.Format("Core.Diagnostics.Attribution.Narrowing.SetReason", Signature));

    public string Headline => Strings.Current.Plural(
        "Core.Diagnostics.Attribution.Narrowing.Headline",
        Candidates.Count,
        TotalModules.ToString(CultureInfo.InvariantCulture));

    public string Describe() => string.Join(
        "\n",
        new[] { Headline, Signature, Mechanism, BoundaryFact }
            .Concat(Steps)
            .Concat(Candidates.Select(candidate => candidate.Describe()))
            .Concat(Contradicted)
            .Concat(Caveats)
            .Concat([Necessity, Containment, Promotion, Disproof])
            .Where(line => line.Length > 0));
}

// FirstChance is what a watch session left behind, and CrashTimeUtc is when this report was written.
// Both are optional and independently so: a trace with no time can still be described, and a time
// with no trace is exactly the case that has to say "nothing was recorded" rather than nothing.
//
// Enhanced, HarmonyCalls, PatchTargets and Boundary are the four inputs the narrowing joins. Every
// one of them is optional, and with all four absent this behaves exactly as it did before they
// existed: the narrowing is simply not produced, and nothing else changes.
public sealed record AttributionInput(
    CrashReport Crash,
    AssemblyIndex Index,
    IReadOnlyList<ModuleId> LoadedModules,
    PatchRegistry? Registry = null,
    string? GameVersion = null,
    int Occurrences = 1,
    FirstChanceTrace? FirstChance = null,
    DateTime? CrashTimeUtc = null,
    EnhancedStacktrace? Enhanced = null,
    HarmonyCallSiteReport? HarmonyCalls = null,
    HarmonyPatchTargetReport? PatchTargets = null,
    InitializationBoundary? Boundary = null);

public sealed record CrashAttribution(
    string ThrownTypeFullName,
    string ThrownMessage,
    CrashFrame? FaultFrame,
    IReadOnlyList<ResolvedFrame> Frames,
    IReadOnlyList<AttributedModule> Suspects,
    IReadOnlyList<AttributedModule> NotSuspected,
    IReadOnlyList<string> Exonerations,
    IReadOnlyList<string> Unknowns,
    int Occurrences = 1,
    AttributionVerdict? Verdict = null,
    CandidateNarrowing? Narrowing = null)
{
    public bool RepeatsDeterministically => Occurrences > 1;
}

public static class Attribution
{
    // A frame further than this above the fault is not an ancestor worth suspecting.
    private const int AncestorReach = 3;

    // Past this many modules at the same band, a list of names is a list of accusations, not an answer.
    private const int ModerateNamingCap = 3;

    // Deliberately loose: it exists to catch a truncated or garbled report, not to be precise.
    private const double UnresolvedShareLimit = 0.3;

    private static string NotACleanBill => Strings.Current["Core.Diagnostics.Attribution.NotACleanBill"];

    private static string Bisect => Strings.Current["Core.Diagnostics.Attribution.Bisect"];

    private static readonly string[] FrameworkPrefixes =
        ["System.", "Microsoft.", "Mono.", "Internal.", "Interop", "Windows."];

    private static readonly string[] EvidenceDestroyingExceptions =
        ["StackOverflowException", "OutOfMemoryException", "ExecutionEngineException"];

    public static CrashAttribution Analyze(AttributionInput input)
    {
        var crash = input.Crash;
        var frames = Resolve(crash, input.Index);
        var fault = frames.FirstOrDefault(f => f.IsRoot && f.Frame.Depth == 0);

        var unknowns = new List<string>();
        var exonerations = new List<string>();

        if (crash.Failed)
            unknowns.Add(crash.Error!);

        var stale = ReadRegistryState(input, unknowns);
        var accused = Accuse(input, frames, fault, stale, unknowns, exonerations);
        var capped = CapDistributedAccusations(accused, fault, out var cap);

        if (cap is not null)
            unknowns.Add(cap);

        var suspects = Rank(capped.Where(m => m.Band > ConfidenceBand.Context));
        var notSuspected = capped.Where(m => m.Band <= ConfidenceBand.Context)
            .Concat(OnTheStack(frames, capped))
            .ToList();

        AddStandingUnknowns(input, frames, unknowns);

        var narrowing = Narrow(input, unknowns);

        return new CrashAttribution(
            crash.Root?.TypeFullName ?? string.Empty,
            crash.Root?.Message ?? string.Empty,
            fault?.Frame,
            frames,
            suspects,
            notSuspected,
            exonerations,
            unknowns,
            input.Occurrences,
            Judge(crash, frames, fault, suspects, stale, cap, IncompleteIndex(input.Index), narrowing),
            narrowing);
    }

    // Assemblies the index could not parse are assemblies no frame can resolve to and no name sweep can
    // clear. A verdict read off a partial index can name the wrong module or clear one it never opened,
    // so the gap travels with the verdict rather than sitting in a list beside it.
    private static string? IncompleteIndex(AssemblyIndex index)
    {
        if (index.Unreadable.Count == 0)
            return null;

        var modules = index.Unreadable
            .Where(u => u.ModuleId is not null)
            .Select(u => u.ModuleId!.Value.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return modules.Count == 0
            ? Strings.Current.Plural("Core.Diagnostics.Attribution.IncompleteIndex.NoOwner", index.Unreadable.Count)
            : Strings.Current.Plural(
                "Core.Diagnostics.Attribution.IncompleteIndex.WithOwner",
                index.Unreadable.Count, modules.Count, string.Join(", ", modules));
    }

    private static string UnreadModules(AssemblyIndex index)
    {
        var modules = index.Unreadable
            .Where(u => u.ModuleId is not null)
            .Select(u => u.ModuleId!.Value.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return modules.Count == 0
            ? string.Empty
            : Strings.Current.Format("Core.Diagnostics.Attribution.UnreadModules", string.Join(", ", modules));
    }

    private static IReadOnlyList<ResolvedFrame> Resolve(CrashReport crash, AssemblyIndex index)
    {
        var resolved = new List<ResolvedFrame>();

        foreach (var node in crash.Nodes)
        {
            foreach (var frame in node.Frames)
                resolved.Add(Resolve(frame, node, index));
        }

        return resolved;
    }

    private static ResolvedFrame Resolve(CrashFrame frame, ExceptionNode node, AssemblyIndex index)
    {
        var (typeName, declarers) = Declarers(frame, index);

        if (declarers.Any(a => a.Origin is AssemblyOrigin.Game))
            return new ResolvedFrame(frame, node, FrameOrigin.Game, typeName, ModuleId.None);

        var owners = declarers
            .Where(a => a.ModuleId is not null)
            .Select(a => a.ModuleId!.Value)
            .Distinct()
            .ToList();

        if (owners.Count == 1)
            return new ResolvedFrame(frame, node, FrameOrigin.Module, typeName, owners[0]);

        // Two modules shipping the same type name cannot be told apart from a frame alone, so the
        // frame stays unresolved rather than accusing whichever one was indexed first.
        var origin = owners.Count == 0 && IsFramework(typeName) ? FrameOrigin.Bcl : FrameOrigin.Unknown;

        return new ResolvedFrame(frame, node, origin, typeName, ModuleId.None);
    }

    // An explicit interface implementation renders as Type.Namespace.IInterface.Method, so the name
    // the parser took as the declaring type can be longer than any real type. Retry shorter prefixes.
    private static (string TypeName, IReadOnlyList<IndexedAssembly> Declarers) Declarers(
        CrashFrame frame,
        AssemblyIndex index)
    {
        var candidate = frame.DeclaringTypeFullName;

        while (candidate.Length > 0)
        {
            var declarers = index.FindDeclaring(candidate);

            if (declarers.Count > 0)
                return (candidate, declarers);

            var lastDot = candidate.LastIndexOf('.');

            if (lastDot <= 0)
                break;

            candidate = candidate[..lastDot];
        }

        return (frame.DeclaringTypeFullName, []);
    }

    private static bool IsFramework(string typeName) =>
        FrameworkPrefixes.Any(prefix => typeName.StartsWith(prefix, StringComparison.Ordinal));

    private static bool ReadRegistryState(AttributionInput input, List<string> unknowns)
    {
        if (input.Registry is null)
        {
            unknowns.Add(Strings.Current["Core.Diagnostics.Attribution.NoRegistryCaptured"]);

            return false;
        }

        var stale = false;
        var captured = new HashSet<ModuleId>(input.Registry.ModuleSet);
        var loaded = new HashSet<ModuleId>(input.LoadedModules);

        if (loaded.Count > 0)
        {
            var added = loaded.Except(captured).Select(m => m.Value).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var removed = captured.Except(loaded).Select(m => m.Value).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var parts = new List<string>();

            if (added.Count > 0)
                parts.Add(Strings.Current.Format("Core.Diagnostics.Attribution.RegistryAddedSince", string.Join(", ", added)));

            if (removed.Count > 0)
                parts.Add(Strings.Current.Format("Core.Diagnostics.Attribution.RegistryRemovedSince", string.Join(", ", removed)));

            if (parts.Count > 0)
            {
                stale = true;
                unknowns.Add(Strings.Current.Format(
                    "Core.Diagnostics.Attribution.RegistryStaleModuleSet", string.Join("; ", parts)));
            }
        }

        if (!GameVersionMatch.SameGameVersion(input.Registry.GameVersion, input.GameVersion))
        {
            stale = true;
            unknowns.Add(Strings.Current.Format(
                "Core.Diagnostics.Attribution.RegistryStaleGameVersion", input.Registry.GameVersion, input.GameVersion));
        }

        return stale;
    }

    private static List<AttributedModule> Accuse(
        AttributionInput input,
        IReadOnlyList<ResolvedFrame> frames,
        ResolvedFrame? fault,
        bool stale,
        List<string> unknowns,
        List<string> exonerations)
    {
        var accused = new List<AttributedModule>();

        if (fault is null)
            return accused;

        if (fault.Origin is FrameOrigin.Module && !fault.ModuleId.IsEmpty)
            accused.Add(OwnCodeAtTheFault(fault));

        if (fault.Frame.IsHarmonyReplacement)
        {
            if (input.Registry is not null)
                accused.AddRange(RegisteredPatchers(input.Registry, fault, stale, unknowns));
            else if (fault.Origin is not FrameOrigin.Module)
                accused.AddRange(SweptPatcher(input.Index, fault, unknowns, exonerations));
        }
        else if (fault.Origin is FrameOrigin.Game && !frames.Any(f => f.IsRoot && f.Origin is FrameOrigin.Module))
        {
            ExonerateByName(input.Index, fault, exonerations);
        }

        return [.. accused.GroupBy(m => m.ModuleId).Select(g => g.MaxBy(m => m.Band)!)];
    }

    private static AttributedModule OwnCodeAtTheFault(ResolvedFrame fault) =>
        new(fault.ModuleId,
            ConfidenceBand.Confirmed,
            "R1",
            Strings.Current.Format("Core.Diagnostics.Attribution.OwnCode.Evidence", fault.Frame.QualifiedName),
            Strings.Current.Format("Core.Diagnostics.Attribution.OwnCode.Disproof", fault.ModuleId),
            fault.Frame.Depth);

    private static IEnumerable<AttributedModule> RegisteredPatchers(
        PatchRegistry registry,
        ResolvedFrame fault,
        bool stale,
        List<string> unknowns)
    {
        var target = $"{fault.ResolvedTypeFullName}.{fault.Frame.OriginalMethodName}";

        var found = registry.PatchersOf(fault.ResolvedTypeFullName, fault.Frame.OriginalMethodName);

        // A patch the capture could not tie to a module is evidence that someone has code here, and
        // no evidence at all about who. It is reported as a gap rather than dropped silently.
        var orphaned = found.Count(p => p.ModuleId.IsEmpty);

        if (orphaned > 0)
        {
            unknowns.Add(Strings.Current.Plural("Core.Diagnostics.Attribution.OrphanedPatches", orphaned, target));
        }

        var byModule = found
            .Where(p => !p.ModuleId.IsEmpty)
            .GroupBy(p => p.ModuleId)
            .ToList();

        if (byModule.Count == 0)
        {
            if (orphaned == 0)
            {
                unknowns.Add(Strings.Current.Format("Core.Diagnostics.Attribution.NoRegistryPatchForMethod", target));
            }

            yield break;
        }

        var collision = byModule.Count > 1;

        if (collision)
        {
            unknowns.Add(Strings.Current.Format(
                "Core.Diagnostics.Attribution.PatchCollisionNoPriority", byModule.Count, target));
        }

        foreach (var module in byModule)
        {
            var kind = module.Min(p => p.Kind);

            // A collision bands every member the same. Which one mattered is a question about the order
            // they ran in, and without priorities that order is unknown, so no member outranks another.
            var band = collision ? ConfidenceBand.Moderate : BandFor(kind);

            yield return new AttributedModule(
                module.Key,
                stale ? Downgrade(band) : band,
                collision ? "R4" : "R3",
                (collision
                    ? Strings.Current.Format(
                        "Core.Diagnostics.Attribution.PatchEvidence.Collision", byModule.Count, target, Describe(kind))
                    : Strings.Current.Format("Core.Diagnostics.Attribution.PatchEvidence.Sole", Describe(kind), target))
                + Rationale(kind),
                collision ? SetDisproof(byModule.Count, target) : Disproof(module.Key, target),
                fault.Frame.Depth,
                kind,
                true);
        }
    }

    // The stack proves the faulting method was replaced by Harmony, so a module patched it. The sweep
    // is only used to say which one, and only when it leaves exactly one candidate. It is never used
    // to accuse a module of anything on an unpatched frame.
    private static IEnumerable<AttributedModule> SweptPatcher(
        AssemblyIndex index,
        ResolvedFrame fault,
        List<string> unknowns,
        List<string> exonerations)
    {
        if (index.Assemblies.All(a => a.Origin is not AssemblyOrigin.Module))
            yield break;

        var target = $"{fault.ResolvedTypeFullName}.{fault.Frame.OriginalMethodName}";
        var candidates = ReferencingModules(index, fault.ResolvedTypeFullName);

        if (candidates.Count == 0)
        {
            unknowns.Add(Strings.Current.Format(
                "Core.Diagnostics.Attribution.SweptNoCandidates", target, fault.ResolvedTypeFullName));

            yield break;
        }

        if (candidates.Count > 1)
        {
            unknowns.Add(Strings.Current.Format(
                "Core.Diagnostics.Attribution.SweptAmbiguous",
                candidates.Count, fault.ResolvedTypeFullName, string.Join(", ", candidates.Select(m => m.Value))));

            yield break;
        }

        exonerations.Add(
            Strings.Current.Format(
                "Core.Diagnostics.Attribution.SweptExoneration", candidates[0], fault.ResolvedTypeFullName)
            + UnreadModules(index));

        yield return new AttributedModule(
            candidates[0],
            ConfidenceBand.Moderate,
            "R3 (name sweep)",
            Strings.Current.Format(
                "Core.Diagnostics.Attribution.SweptEvidence", target, candidates[0], fault.ResolvedTypeFullName),
            Disproof(candidates[0], target),
            fault.Frame.Depth);
    }

    private static void ExonerateByName(AssemblyIndex index, ResolvedFrame fault, List<string> exonerations)
    {
        if (index.Assemblies.All(a => a.Origin is not AssemblyOrigin.Module))
            return;

        if (ReferencingModules(index, fault.ResolvedTypeFullName).Count > 0)
            return;

        exonerations.Add(
            Strings.Current.Format("Core.Diagnostics.Attribution.ExonerateByName", fault.ResolvedTypeFullName)
            + UnreadModules(index));
    }

    private static IReadOnlyList<ModuleId> ReferencingModules(AssemblyIndex index, string typeFullName) =>
    [
        .. index.FindReferencing(typeFullName)
            .Where(a => a.Origin is AssemblyOrigin.Module && a.ModuleId is not null)
            .Select(a => a.ModuleId!.Value)
            .Distinct()
            .Order(Comparer<ModuleId>.Create((x, y) => StringComparer.OrdinalIgnoreCase.Compare(x.Value, y.Value)))
    ];

    private static ConfidenceBand BandFor(PatchKind kind) => kind switch
    {
        PatchKind.Transpiler => ConfidenceBand.Strong,
        PatchKind.Prefix => ConfidenceBand.Moderate,
        _ => ConfidenceBand.Weak
    };

    private static ConfidenceBand Downgrade(ConfidenceBand band) => band switch
    {
        ConfidenceBand.Confirmed => ConfidenceBand.Strong,
        ConfidenceBand.Strong => ConfidenceBand.Moderate,
        ConfidenceBand.Moderate => ConfidenceBand.Weak,
        ConfidenceBand.Weak => ConfidenceBand.Context,
        _ => band
    };

    private static string Describe(PatchKind kind) => kind.ToString().ToLowerInvariant();

    private static string Rationale(PatchKind kind) => kind switch
    {
        PatchKind.Transpiler => Strings.Current["Core.Diagnostics.Attribution.Rationale.Transpiler"],
        PatchKind.Prefix => Strings.Current["Core.Diagnostics.Attribution.Rationale.Prefix"],
        PatchKind.Finalizer => Strings.Current["Core.Diagnostics.Attribution.Rationale.Finalizer"],
        _ => Strings.Current["Core.Diagnostics.Attribution.Rationale.Postfix"]
    };

    private static string Disproof(ModuleId module, string target) =>
        Strings.Current.Format("Core.Diagnostics.Attribution.Disproof.Single", module, target);

    private static string SetDisproof(int count, string target) =>
        Strings.Current.Format("Core.Diagnostics.Attribution.Disproof.Set", count, target);

    private static List<AttributedModule> OnTheStack(
        IReadOnlyList<ResolvedFrame> frames,
        IReadOnlyList<AttributedModule> accused)
    {
        var named = new HashSet<ModuleId>(accused.Select(m => m.ModuleId));
        var context = new List<AttributedModule>();

        foreach (var module in frames
                     .Where(f => f.Origin is FrameOrigin.Module && !f.ModuleId.IsEmpty)
                     .Select(f => f.ModuleId)
                     .Distinct())
        {
            if (!named.Add(module))
                continue;

            context.Add(AsContext(module, frames));
        }

        return context;
    }

    private static AttributedModule AsContext(ModuleId module, IReadOnlyList<ResolvedFrame> frames)
    {
        var own = frames.Where(f => f.ModuleId == module).ToList();
        var root = own.Where(f => f.IsRoot).ToList();
        var depth = (root.Count > 0 ? root : own).Min(f => f.Frame.Depth);
        var trigger = PathTrigger(module, frames);

        var placement = root.Count == 0
            ? Strings.Current.Format("Core.Diagnostics.Attribution.Context.WrappingOnly", own[0].Node.Label)
            : string.Empty;

        if (trigger is not null)
        {
            return new AttributedModule(
                module,
                ConfidenceBand.Context,
                "R6b",
                placement + Strings.Current.Format("Core.Diagnostics.Attribution.Context.PathTrigger", trigger),
                Strings.Current["Core.Diagnostics.Attribution.Context.PathTriggerDisproof"],
                depth);
        }

        if (root.Count == 0)
        {
            return new AttributedModule(
                module,
                ConfidenceBand.Context,
                "R6",
                placement + Strings.Current["Core.Diagnostics.Attribution.Context.Structural"],
                Strings.Current["Core.Diagnostics.Attribution.Context.OnStackDisproof"],
                depth);
        }

        if (depth > AncestorReach)
        {
            return new AttributedModule(
                module,
                ConfidenceBand.Context,
                "R6",
                Strings.Current.Format("Core.Diagnostics.Attribution.Context.TooFar", depth),
                Strings.Current["Core.Diagnostics.Attribution.Context.OnStackDisproof"],
                depth);
        }

        return new AttributedModule(
            module,
            ConfidenceBand.Context,
            "none",
            Strings.Current.Format("Core.Diagnostics.Attribution.Context.OnPath", depth),
            Strings.Current["Core.Diagnostics.Attribution.Context.CaptureRegistry"],
            depth);
    }

    private static string? PathTrigger(ModuleId module, IReadOnlyList<ResolvedFrame> frames)
    {
        for (var i = 1; i < frames.Count - 1; i++)
        {
            var before = frames[i - 1];
            var after = frames[i + 1];

            if (frames[i].ModuleId != module || frames[i].Node != before.Node || frames[i].Node != after.Node)
                continue;

            if (before.Frame.IsHarmonyReplacement
                && string.Equals(before.Frame.QualifiedName, after.Frame.QualifiedName, StringComparison.Ordinal))
            {
                return $"{before.ResolvedTypeFullName}.{before.Frame.OriginalMethodName}";
            }
        }

        return null;
    }

    private static IReadOnlyList<AttributedModule> Rank(IEnumerable<AttributedModule> suspects) =>
    [
        .. suspects
            .OrderByDescending(s => s.Band)
            .ThenBy(s => s.Depth)
            .ThenBy(s => s.Kind is null ? int.MaxValue : (int)s.Kind)
            .ThenBy(s => s.ModuleId.Value, StringComparer.OrdinalIgnoreCase)
    ];

    private static List<AttributedModule> CapDistributedAccusations(
        List<AttributedModule> accused,
        ResolvedFrame? fault,
        out string? cap)
    {
        var moderate = accused.Where(m => m.Band is ConfidenceBand.Moderate).ToList();
        cap = null;

        if (moderate.Count <= ModerateNamingCap)
            return accused;

        var target = fault is null
            ? Strings.Current["Core.Diagnostics.Attribution.MethodThatThrew"]
            : Strings.Current.Format(
                "Core.Diagnostics.Attribution.MethodThatThrewNamed",
                fault.ResolvedTypeFullName, fault.Frame.OriginalMethodName);

        cap = Strings.Current.Format("Core.Diagnostics.Attribution.ModerateCap", moderate.Count, target);

        return [.. accused.Except(moderate)];
    }

    // The join. Three things the user already has on disk, intersected:
    //
    //   - which modules call the exact method the fault frame names, at the exact arity it names,
    //     read from assembly metadata with no mod code loaded,
    //   - which modules this run's own logs show had not yet been observed loading,
    //   - and any static finding that names one of those modules.
    //
    // Every candidate is put through InitializationBoundary.Contradicts before it is shown, which is
    // the guard that stops a static finding naming a module the same crash folder proves was already
    // running. Nothing here produces a verdict, and the head of the list is never promoted to one.
    private static CandidateNarrowing? Narrow(AttributionInput input, List<string> unknowns)
    {
        if (Fault(input) is not { } fault)
            return null;

        if (!HarmonyCallSites.ResolvesAssemblyFromStack(fault.Type, fault.Method, fault.Parameters))
        {
            if (HarmonyCallSites.IsPatchEntryPoint(fault.Type, fault.Method))
            {
                var parameterCount = Strings.Current.Plural(
                    "Core.Diagnostics.Attribution.ParameterCount", fault.Parameters);

                unknowns.Add(Strings.Current.Format(
                    "Core.Diagnostics.Attribution.Narrow.KnownAssemblyOverload",
                    fault.Type, fault.Method, parameterCount));
            }

            return null;
        }

        var signature = $"{fault.Type}.{fault.Method}({Placeholders(fault.Parameters)})";

        if (input.HarmonyCalls is not { Looked: true } calls)
        {
            unknowns.Add(Strings.Current.Format("Core.Diagnostics.Attribution.Narrow.NoModuleAssemblies", signature));

            return null;
        }

        if (input.Boundary is not { } boundary)
        {
            unknowns.Add(Strings.Current.Plural(
                "Core.Diagnostics.Attribution.Narrow.NoBoundary",
                calls.Callers(fault.Type, fault.Method, fault.Parameters).Count, signature));

            return null;
        }

        var callers = calls.Callers(fault.Type, fault.Method, fault.Parameters);
        var order = boundary.HadInitialized.Concat(boundary.NotObserved).ToList();
        var candidates = new List<CandidateModule>();
        var contradicted = new List<string>();

        foreach (var module in callers)
        {
            if (boundary.Contradicts(module.Value) is { } already)
            {
                contradicted.Add(already);
                continue;
            }

            var position = order.FindIndex(id => string.Equals(id, module.Value, StringComparison.OrdinalIgnoreCase));

            if (position < 0)
            {
                contradicted.Add(Strings.Current.Format(
                    "Core.Diagnostics.Attribution.Narrow.NotOnCommandLine", module, signature));

                continue;
            }

            candidates.Add(new CandidateModule(
                module,
                position + 1,
                signature,
                Corroborate(input.PatchTargets, module)));
        }

        if (candidates.Count == 0)
            return null;

        // Load order position, which is the order the user's own launcher shows them, and nothing
        // else. It is a way to find a name in the list rather than a statement about any of them.
        var listed = candidates
            .OrderBy(candidate => candidate.Position)
            .ThenBy(candidate => candidate.ModuleId.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CandidateNarrowing(
            signature,
            Mechanism(signature, fault),
            boundary.TotalModules,
            boundary.HadInitialized.Count,
            boundary.NotObserved.Count,
            callers.Count,
            listed,
            Steps(signature, boundary, callers.Count, listed.Count),
            contradicted,
            Caveats(input, boundary),
            BoundaryFact(boundary),
            Necessity(signature, callers.Count),
            Containment(signature, listed.Count),
            Promotion(listed.Count),
            Disproof(signature, listed.Count));
    }

    private static (string Type, string Method, int Parameters, string ModuleId, string IlOffset)? Fault(
        AttributionInput input)
    {
        // The enhanced report first, because it writes the signature in a form with no ambiguity in
        // it. The plain frame is the fallback and carries the parameter list too, which is the field
        // the whole narrowing turns on.
        if (input.Enhanced?.Fault is { } enhanced && enhanced.MethodName.Length > 0)
        {
            return (
                enhanced.DeclaringTypeFullName,
                enhanced.MethodName,
                enhanced.ParameterCount,
                enhanced.ModuleId,
                enhanced.IlOffset);
        }

        if (input.Crash.FaultFrame is not { } frame)
            return null;

        var (type, method, _) = EnhancedStacktrace.Split(string.Empty, frame.QualifiedName);

        return (type, method, EnhancedStacktrace.CountParameters(frame.Parameters), string.Empty, string.Empty);
    }

    private static string Mechanism(
        string signature,
        (string Type, string Method, int Parameters, string ModuleId, string IlOffset) fault)
    {
        // Read out of the IL of the 0Harmony this install ships rather than from documentation: the
        // parameterless overloads open with new StackTrace(), GetFrame(1), GetMethod(), and then
        // dereference that result with a callvirt to get_ReflectedType. Nothing between the two
        // checks it for null.
        var lines = new List<string>
        {
            Strings.Current.Format("Core.Diagnostics.Attribution.Mechanism.Core", signature)
        };

        if (fault.ModuleId.Length > 0)
        {
            lines.Add(Strings.Current.Format("Core.Diagnostics.Attribution.Mechanism.ModuleAttributed", fault.ModuleId));
        }

        if (fault.IlOffset.Length > 0)
        {
            lines.Add(Strings.Current.Format("Core.Diagnostics.Attribution.Mechanism.IlOffset", fault.IlOffset));
        }

        return string.Join(" ", lines);
    }

    private static IReadOnlyList<string> Steps(
        string signature,
        InitializationBoundary boundary,
        int callers,
        int candidates) =>
    [
        Strings.Current.Plural("Core.Diagnostics.Attribution.Steps.CommandLine", boundary.TotalModules),

        Strings.Current.Format(
            "Core.Diagnostics.Attribution.Steps.Furthest",
            Number(boundary.HadInitialized.Count), boundary.Furthest.ModuleId, Number(boundary.Furthest.Position)),

        Strings.Current.Format("Core.Diagnostics.Attribution.Steps.NotObserved", Number(boundary.NotObserved.Count)),

        Strings.Current.Plural("Core.Diagnostics.Attribution.Steps.Callers", callers, signature),

        Strings.Current.Format("Core.Diagnostics.Attribution.Steps.Intersection", Number(candidates))
    ];

    // The one fact the boundary supports, said with no ordering attached to it. The sentence stops
    // exactly where the evidence does: this many modules finished, and that is all it says.
    private static string BoundaryFact(InitializationBoundary boundary) => Strings.Current.Format(
        "Core.Diagnostics.Attribution.BoundaryFact",
        boundary.Furthest.ModuleId, Number(boundary.Furthest.Position), Number(boundary.TotalModules));

    private static IReadOnlyList<string> Caveats(AttributionInput input, InitializationBoundary boundary)
    {
        var caveats = new List<string>
        {
            Strings.Current.Format("Core.Diagnostics.Attribution.Caveat.Floor", Number(boundary.HadInitialized.Count)),
            Strings.Current["Core.Diagnostics.Attribution.Caveat.Position"],
            Strings.Current["Core.Diagnostics.Attribution.Caveat.MoreThanOne"]
        };

        if (boundary.Silence is { } quiet)
        {
            // Selects on the whole seconds and prints the tenth, so "1.0 second" and "2.4 seconds"
            // both read correctly.
            caveats.Add(Strings.Current.Plural(
                "Core.Diagnostics.Attribution.Caveat.Silence",
                (long)Math.Round(quiet.TotalSeconds),
                quiet.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)));
        }

        if (Diverged(input, boundary) is { } diverged)
            caveats.Add(diverged);

        return caveats;
    }

    // A position only means anything against the order that was actually launched. If the launcher's
    // list has moved since, every number here still comes from the crash folder, and the reader has to
    // be told that before they check one against what their launcher shows them now.
    private static string? Diverged(AttributionInput input, InitializationBoundary boundary)
    {
        if (input.LoadedModules.Count == 0)
            return null;

        var launched = boundary.HadInitialized.Concat(boundary.NotObserved).ToList();
        var now = input.LoadedModules.Select(module => module.Value).ToList();

        if (launched.SequenceEqual(now, StringComparer.OrdinalIgnoreCase))
            return null;

        return Strings.Current.Plural(
            "Core.Diagnostics.Attribution.Diverged", launched.Count, Number(now.Count));
    }

    private static string Corroborate(HarmonyPatchTargetReport? targets, ModuleId module)
    {
        var findings = targets?.Findings.Where(finding => finding.ModuleId == module).ToList() ?? [];

        if (findings.Count == 0)
            return string.Empty;

        return Strings.Current.Plural(
            "Core.Diagnostics.Attribution.Corroborate", findings.Count, findings[0].Describe());
    }

    private static string Necessity(string signature, int callers) => Strings.Current.Plural(
        "Core.Diagnostics.Attribution.Necessity", callers, signature);

    // The experiment that turns a set into a proof rather than a suggestion, and the only claim BEM
    // is entitled to make from a set: not who, but where.
    private static string Containment(string signature, int candidates) => Strings.Current.Format(
        "Core.Diagnostics.Attribution.Containment", Number(candidates));

    // What it would take to get from the set to one name, in both directions, because a launch that
    // removed a module and did not crash proves nothing on its own: the crash has to come back when
    // the module does.
    private static string Promotion(int candidates) => Strings.Current.Format(
        "Core.Diagnostics.Attribution.Promotion", Number(candidates));

    private static string Disproof(string signature, int candidates) => Strings.Current.Format(
        "Core.Diagnostics.Attribution.NarrowingDisproof", Number(candidates), signature);

    private static string Placeholders(int parameters) => parameters switch
    {
        0 => string.Empty,
        1 => "string",
        _ => string.Join(", ", Enumerable.Repeat("?", parameters))
    };

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static AttributionVerdict Judge(
        CrashReport crash,
        IReadOnlyList<ResolvedFrame> frames,
        ResolvedFrame? fault,
        IReadOnlyList<AttributedModule> suspects,
        bool stale,
        string? cap,
        string? incompleteIndex,
        CandidateNarrowing? narrowing)
    {
        var thrown = crash.Root is null || crash.Root.TypeFullName.Length == 0
            ? Strings.Current["Core.Diagnostics.Attribution.UnknownException"]
            : crash.Root.TypeFullName;

        var opening = fault is null
            ? Strings.Current.Format("Core.Diagnostics.Attribution.Opening.NoFault", thrown)
            : Strings.Current.Format("Core.Diagnostics.Attribution.Opening.WithFault", thrown, fault.Frame.QualifiedName);

        var reasons = WhatStopsAnAnswer(crash, frames, fault, suspects, stale, cap);
        string[] partial = incompleteIndex is null ? [] : [incompleteIndex];

        // A set is stated as a set wherever it appears, including here. It never becomes the headline
        // and it never supplies a name to one: no member of it outranks any other, so there is no
        // first name for a headline to reach for.
        string[] narrowed = narrowing is null
            ? []
            :
            [
                Strings.Current.Format(
                    "Core.Diagnostics.Attribution.NarrowedSummary",
                    narrowing.Signature, Number(narrowing.TotalModules), Number(narrowing.Candidates.Count))
            ];

        if (reasons.Count > 0)
        {
            return new AttributionVerdict(
                VerdictKind.Unknown,
                Strings.Current["Core.Diagnostics.Attribution.Headline.Unknown"],
                [opening, .. reasons, .. narrowed, .. partial, NotACleanBill],
                narrowing is null ? Bisect : narrowing.Containment);
        }

        var top = suspects[0];

        if (top.Band >= ConfidenceBand.Strong)
        {
            return new AttributionVerdict(
                VerdictKind.Named,
                top.Band is ConfidenceBand.Confirmed
                    ? Strings.Current.Format("Core.Diagnostics.Attribution.Headline.OwnCode", top.ModuleId)
                    : Strings.Current.Format("Core.Diagnostics.Attribution.Headline.MostLikely", top.ModuleId),
                [
                    opening, top.Evidence,
                    Strings.Current["Core.Diagnostics.Attribution.Suspect.NotVerdict"],
                    .. narrowed, .. partial
                ],
                top.Band is ConfidenceBand.Confirmed
                    ? Strings.Current.Format("Core.Diagnostics.Attribution.NextStep.DisableOwnCode", top.ModuleId)
                    : Strings.Current.Format("Core.Diagnostics.Attribution.NextStep.RuleOut", top.Disproof),
                top.ModuleId);
        }

        return new AttributionVerdict(
            VerdictKind.Suspected,
            Strings.Current.Format("Core.Diagnostics.Attribution.Headline.WeakestBand", Describe(top.Band)),
            [
                opening,
                Strings.Current.Format("Core.Diagnostics.Attribution.RankedListSummary", Count(suspects.Count)),
                .. narrowed,
                .. partial
            ],
            Strings.Current.Format("Core.Diagnostics.Attribution.NextStep.RuleOutFirst", top.Disproof));
    }

    private static List<string> WhatStopsAnAnswer(
        CrashReport crash,
        IReadOnlyList<ResolvedFrame> frames,
        ResolvedFrame? fault,
        IReadOnlyList<AttributedModule> suspects,
        bool stale,
        string? cap)
    {
        var reasons = new List<string>();
        var root = frames.Where(f => f.IsRoot).ToList();

        if (crash.Failed)
            reasons.Add(crash.Error!);

        if (root.Count == 0)
        {
            reasons.Add(Strings.Current["Core.Diagnostics.Attribution.NoStackTrace"]);
        }

        var thrown = SimpleName(crash.Root?.TypeFullName);

        if (EvidenceDestroyingExceptions.Contains(thrown, StringComparer.Ordinal))
        {
            reasons.Add(Strings.Current.Format("Core.Diagnostics.Attribution.DestroyedStack", thrown));
        }

        if (fault is not null && fault.Origin is FrameOrigin.Unknown)
        {
            reasons.Add(Strings.Current.Format("Core.Diagnostics.Attribution.UnknownFrame", fault.Frame.QualifiedName));
        }

        var unresolved = root.Count(f => f.Origin is FrameOrigin.Unknown);

        if (root.Count > 0 && unresolved > root.Count * UnresolvedShareLimit)
        {
            reasons.Add(Strings.Current.Format("Core.Diagnostics.Attribution.TooManyUnresolved", unresolved, root.Count));
        }

        if (stale && suspects.Count > 0 && suspects.All(s => s.FromRegistry))
        {
            reasons.Add(Strings.Current["Core.Diagnostics.Attribution.StaleRegistryBands"]);
        }

        if (cap is not null)
            reasons.Add(cap);

        if (!suspects.Any(s => s.Band > ConfidenceBand.Weak))
        {
            reasons.Add(Strings.Current["Core.Diagnostics.Attribution.NoStrongRule"]);
        }

        return reasons;
    }

    private static string SimpleName(string? typeFullName)
    {
        var name = typeFullName ?? string.Empty;
        var lastDot = name.LastIndexOf('.');

        return lastDot < 0 ? name : name[(lastDot + 1)..];
    }

    private static string Count(int suspects) =>
        Strings.Current.Plural("Core.Diagnostics.Attribution.SuspectCount", suspects);

    private static string Describe(ConfidenceBand band) => band.ToString().ToLowerInvariant();

    private static void AddStandingUnknowns(
        AttributionInput input,
        IReadOnlyList<ResolvedFrame> frames,
        List<string> unknowns)
    {
        if (input.Index.Failed)
            unknowns.Add(input.Index.Error!);

        if (IncompleteIndex(input.Index) is { } partial)
            unknowns.Add(partial);

        if (input.Index.Assemblies.All(a => a.Origin is not AssemblyOrigin.Module))
        {
            unknowns.Add(Strings.Current["Core.Diagnostics.Attribution.NoModuleIndexed"]);
        }

        var root = frames.Where(f => f.IsRoot).ToList();
        var unresolved = root.Count(f => f.Origin is FrameOrigin.Unknown);

        if (unresolved > 0)
        {
            unknowns.Add(Strings.Current.Format("Core.Diagnostics.Attribution.UnresolvedFrames", unresolved, root.Count));
        }

        unknowns.Add(DescribeFirstChance(input));
    }

    // Three different sentences, because "nothing was recorded", "something was recorded but cannot
    // be lined up with this crash" and "something was recorded and nothing landed near this crash"
    // mean three different things to the reader. The first of them used to be printed unconditionally,
    // including on reports that did have a trace.
    private static string DescribeFirstChance(AttributionInput input)
    {
        if (input.FirstChance is not { } trace)
        {
            return Strings.Current["Core.Diagnostics.Attribution.FirstChance.NoTrace"];
        }

        if (input.CrashTimeUtc is not { } crashed)
        {
            return Strings.Current.Plural(
                "Core.Diagnostics.Attribution.FirstChance.NoTime", trace.Captured, trace.RunId);
        }

        var swallowed = trace.SwallowedBefore(crashed);
        var seconds = FirstChanceTrace.DefaultWindow.TotalSeconds;

        if (swallowed.Count == 0)
        {
            return Strings.Current.Plural(
                "Core.Diagnostics.Attribution.FirstChance.NoneNearby",
                trace.Captured, trace.RunId, seconds.ToString("0", CultureInfo.InvariantCulture));
        }

        return Strings.Current.Plural(
            "Core.Diagnostics.Attribution.FirstChance.Swallowed",
            swallowed.Count,
            seconds.ToString("0", CultureInfo.InvariantCulture),
            string.Join(" ", swallowed.Select(record => record.Describe())));
    }
}
