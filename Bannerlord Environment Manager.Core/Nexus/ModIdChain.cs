namespace BannerlordEnvironmentManager.Core.Nexus;

// The six ways BEM can learn which Nexus page a module came from, in the one order Find Mod IDs runs
// them: the free ones first, then the ones that spend the owner's request allowance. The order is the
// enum's order, so a caller cannot run a spending step ahead of a free one by listing it first.
public enum ModIdStepKind
{
    // The id another manager (MO2's meta.ini) already recorded for the mod it installed.
    OtherManagers = 1,
    ArchiveNames = 2,
    DeletedArchiveNames = 3,
    ButrIndex = 4,
    UnlinkedArchives = 5,
    FileHash = 6,
    UnrecognizedAgain = 7,
}

// One step of the chain. Run receives only the modules still without an id and returns a line saying
// what it did; what it actually solved is measured afterwards by the chain, not trusted from the step.
// EstimateCost says how many requests the step would spend on those modules, and is read only for a
// step that spends.
public sealed record ModIdStep(
    ModIdStepKind Kind,
    bool SpendsRequests,
    Func<IReadOnlyList<string>, CancellationToken, Task<string?>> Run,
    Func<IReadOnlyList<string>, int>? EstimateCost = null);

public sealed record ModIdStepReport(ModIdStepKind Kind, int Given, int Solved, string? Message);

public sealed record ModIdChainResult(
    int Targets,
    IReadOnlyList<ModIdStepReport> Steps,
    IReadOnlyList<string> StillUnresolved,
    // True when the owner turned the spending steps down; the free steps' results stand.
    bool SpendingDeclined)
{
    public int Solved => Targets - StillUnresolved.Count;
}

public static class ModIdChain
{
    // Which modules the chain works on: every module with no Nexus id that is not excluded from
    // checking. With rebuild on, every module that is not excluded, because the owner asked for every
    // id to be worked out again.
    public static IReadOnlyList<string> Targets(IEnumerable<NexusModuleLink> links, bool rebuild)
    {
        ArgumentNullException.ThrowIfNull(links);

        return
        [
            .. links
                .Where(link => !link.IsExcluded && (rebuild || link.NexusModId is null))
                .Select(link => link.ModuleId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];
    }

    // Runs every step in ModIdStepKind order. Each step is handed only what the earlier steps left
    // unresolved, as stillUnresolved reports it after the step before; a step with nothing to work on
    // is not run at all. Before the first spending step that has anything to work on, confirmSpend is
    // asked once with the summed cost of every spending step on what is left; a no ends the chain there.
    public static async Task<ModIdChainResult> RunAsync(
        IReadOnlyList<string> targets,
        IEnumerable<ModIdStep> steps,
        Func<IReadOnlyList<string>, IReadOnlyList<string>> stillUnresolved,
        Func<int, Task<bool>> confirmSpend,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(stillUnresolved);
        ArgumentNullException.ThrowIfNull(confirmSpend);

        var ordered = steps.OrderBy(step => (int)step.Kind).ToList();
        var remaining = targets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var reports = new List<ModIdStepReport>();
        var spendingConfirmed = false;

        for (var i = 0; i < ordered.Count && remaining.Count > 0; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var step = ordered[i];

            if (step.SpendsRequests && !spendingConfirmed)
            {
                var cost = ordered.Skip(i)
                    .Where(later => later.SpendsRequests)
                    .Sum(later => Math.Max(later.EstimateCost?.Invoke(remaining) ?? 0, 0));

                if (!await confirmSpend(cost))
                    return new ModIdChainResult(targets.Count, reports, remaining, SpendingDeclined: true);

                spendingConfirmed = true;
            }

            var given = remaining;
            var message = await step.Run(given, cancellationToken);
            var left = stillUnresolved(given);

            // A step can only narrow what it was given: nothing it did can put a module back in.
            remaining = [.. given.Where(module => left.Contains(module, StringComparer.OrdinalIgnoreCase))];

            reports.Add(new ModIdStepReport(step.Kind, given.Count, given.Count - remaining.Count, message));
        }

        return new ModIdChainResult(targets.Count, reports, remaining, SpendingDeclined: false);
    }
}
