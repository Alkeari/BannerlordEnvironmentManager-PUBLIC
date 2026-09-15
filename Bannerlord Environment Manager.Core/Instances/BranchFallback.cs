using System.Text.RegularExpressions;

namespace BannerlordEnvironmentManager.Core.Instances;

// One depot Steam had no such branch for. DepotDownloader does not stop over this and does not fail:
// it says so once and quietly fetches that depot from another branch, so a download asked for on
// "beta" can land with two of its four depots carrying whatever the public branch is publishing
// today. Nothing else in BEM could see that happen, because the only record of it was a line in the
// tool's own output.
public sealed record BranchFallback(string DepotId, string RequestedBranch, string UsedBranch);

public static partial class BranchFallbackReader
{
    // DepotDownloader's own sentence, matched as it writes it:
    // Warning: Depot 228988 does not have branch named "beta". Trying public branch.
    [GeneratedRegex(
        @"Depot (?<depot>\d+) does not have branch named ""(?<requested>[^""]+)""\.\s*Trying (?<used>\S+) branch",
        RegexOptions.IgnoreCase)]
    private static partial Regex FallbackPattern { get; }

    public static IReadOnlyList<BranchFallback> ReadAll(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var found = new List<BranchFallback>();

        foreach (var line in lines)
        {
            if (line is null)
                continue;

            var match = FallbackPattern.Match(line);

            if (!match.Success)
                continue;

            var fallback = new BranchFallback(
                match.Groups["depot"].Value,
                match.Groups["requested"].Value,
                match.Groups["used"].Value);

            // The same warning is printed once per depot, but a resumed run replays the whole
            // sequence, and one depot named twice would read as two different problems.
            if (!found.Contains(fallback))
                found.Add(fallback);
        }

        return found;
    }

    // "228988, 229006" - what the user is shown, so the depots can be matched against the tool log
    // rather than merely alluded to.
    public static string DepotList(IReadOnlyList<BranchFallback> fallbacks)
    {
        ArgumentNullException.ThrowIfNull(fallbacks);

        return string.Join(", ", fallbacks.Select(fallback => fallback.DepotId));
    }
}
