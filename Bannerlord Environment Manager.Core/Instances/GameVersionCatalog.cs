using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Instances;

// One game version a user can pick. Branch is Steam's name for it and never reaches the user: a
// branch name is not a version, and most of Bannerlord's branches (local, perf_test, the launcher
// variants) are not versions of the game at all.
public sealed record GameVersionOption(ModuleVersion Version, string Branch, string BuildId)
{
    public string Label => Version.ToString();
}

// Turns Steam's branch list into the version list. A branch earns a place only when its version can
// be named exactly: from the branch name, from a description that is nothing but a version, or, for
// the public branch, from the version the caller read off the install that branch produced.
public static partial class GameVersionCatalog
{
    private const string BetaBranch = "beta";

    [GeneratedRegex(@"^[ve]\d+\.\d+\.\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionName { get; }

    // "Beta v1.5.2" resolves; "v1.3.7 Watchdog Launcher" and "Bannerlord Performance Test" do not,
    // because a description carrying anything besides the version describes a variant build rather
    // than the version itself.
    [GeneratedRegex(@"^(?:Beta\s+)?(?<version>[ve]\d+\.\d+\.\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionDescription { get; }

    public static IReadOnlyList<GameVersionOption> From(
        IReadOnlyList<SteamBranch> branches, ModuleVersion publicVersion)
    {
        ArgumentNullException.ThrowIfNull(branches);

        var found = new List<(int Priority, GameVersionOption Option)>();

        foreach (var branch in branches)
        {
            if (branch.NeedsPassword)
                continue;

            var version = VersionFor(branch, publicVersion);

            if (version.IsEmpty)
                continue;

            found.Add((PriorityFor(branch.Name), new GameVersionOption(version, branch.Name, branch.BuildId)));
        }

        // Two branches can carry the same version: public and beta both move over time and can land
        // on a version a pinned legacy branch already publishes. The branch a user would expect to
        // get wins, which is the live one rather than the frozen copy of it.
        return
        [
            .. found
                .OrderBy(entry => entry.Priority)
                .GroupBy(entry => entry.Option.Version)
                .Select(group => group.First().Option)
                .OrderByDescending(option => SeriesRank(option.Version.Type))
                .ThenByDescending(option => option.Version)
        ];
    }

    // The release series (v1.0.0 onward) is newer than every early access version (e1.9.0 and below)
    // whatever the numbers say: TaleWorlds restarted at 1.0.0 when the game left early access, so
    // comparing the numbers alone puts the oldest builds at the top of the list. Public so every
    // list of versions in the app sorts the one way.
    public static int SeriesRank(ModuleVersionType type) => type switch
    {
        ModuleVersionType.Release => 2,
        ModuleVersionType.EarlyAccess => 1,
        _ => 0
    };

    private static ModuleVersion VersionFor(SteamBranch branch, ModuleVersion publicVersion)
    {
        if (string.Equals(branch.Name, SteamAppManifest.PublicBranch, StringComparison.OrdinalIgnoreCase))
            return publicVersion;

        if (VersionName.IsMatch(branch.Name))
            return ModuleVersion.Parse(branch.Name);

        var description = VersionDescription.Match(branch.Description.Trim());

        return description.Success
            ? ModuleVersion.Parse(description.Groups["version"].Value)
            : ModuleVersion.Empty;
    }

    private static int PriorityFor(string name) => name switch
    {
        SteamAppManifest.PublicBranch => 0,
        BetaBranch => 1,
        _ => 2
    };
}
