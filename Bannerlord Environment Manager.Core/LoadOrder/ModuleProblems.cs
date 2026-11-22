using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.LoadOrder;

public enum ModuleProblemSource
{
    LoadOrder,
    Finding,
}

// A Health finding that names one module, in the shape this mapping needs: the app builds one from each
// open finding whose check knows which module it is about, and keeps the finding itself beside it (by
// Key) so its existing fix actions are what the row's button runs.
public sealed record ModuleFinding(ModuleId ModuleId, IssueSeverity Severity, string Headline, string Key);

// One reason a module cannot work, ready for its own row on Play. Exactly one of Issue and Finding is
// set, matching Source, so the app reuses the existing fix of whichever it is rather than inventing one.
public sealed record ModuleProblem(
    ModuleId ModuleId,
    ModuleProblemSource Source,
    IssueSeverity Severity,
    string Reason,
    LoadOrderIssue? Issue,
    ModuleFinding? Finding);

// The one place a finding is tied to a module for the Play list. A missing requirement or the wrong game
// version is a load-order issue; every other Health finding arrives as a ModuleFinding. Notes never reach
// a row, an identical reason is said once, and a module shows at most MaxPerModule so a badly broken mod
// does not turn its row into a page; the rest stay on Health.
public static class ModuleProblems
{
    public const int MaxPerModule = 3;

    public const int MaxReasonLength = 160;

    public static IReadOnlyDictionary<ModuleId, IReadOnlyList<ModuleProblem>> Group(
        IEnumerable<LoadOrderIssue> issues,
        IEnumerable<ModuleFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(findings);

        var all = new List<ModuleProblem>();

        foreach (var issue in issues)
        {
            if (issue.Severity == IssueSeverity.Information)
                continue;

            all.Add(new ModuleProblem(issue.ModuleId, ModuleProblemSource.LoadOrder, issue.Severity, Shorten(issue.Message), issue, null));
        }

        foreach (var finding in findings)
        {
            if (finding.Severity == IssueSeverity.Information || string.IsNullOrWhiteSpace(finding.ModuleId.Value))
                continue;

            all.Add(new ModuleProblem(finding.ModuleId, ModuleProblemSource.Finding, finding.Severity, Shorten(finding.Headline), null, finding));
        }

        var result = new Dictionary<ModuleId, IReadOnlyList<ModuleProblem>>();

        foreach (var group in all.GroupBy(p => p.ModuleId))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var kept = group
                .Select((problem, order) => (problem, order))
                .OrderBy(x => x.problem.Severity == IssueSeverity.Error ? 0 : 1)
                .ThenBy(x => x.problem.Source)
                .ThenBy(x => x.order)
                .Select(x => x.problem)
                .Where(p => seen.Add(p.Reason))
                .Take(MaxPerModule)
                .ToList();

            if (kept.Count > 0)
                result[group.Key] = kept;
        }

        return result;
    }

    // The first sentence, trimmed: the row has one line for the reason, and Health keeps the rest.
    public static string Shorten(string text)
    {
        var line = (text ?? string.Empty).Trim().ReplaceLineEndings(" ");
        var stop = line.IndexOf(". ", StringComparison.Ordinal);

        if (stop > 0)
            line = line[..(stop + 1)];

        return line.Length <= MaxReasonLength ? line : line[..(MaxReasonLength - 1)].TrimEnd() + "…";
    }
}
