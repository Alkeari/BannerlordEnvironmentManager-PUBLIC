using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.DryRun;

public sealed record CompanionPayloadLookup(
    string? Path,
    string Reason,
    string? PlatformFolder = null,
    bool MatchesInstallPlatform = true)
{
    public bool Found => Path is not null;
}

// Where BEM's copy of the companion assembly lives before it is installed. It is built against the
// user's own game assemblies for one platform at a time, so it travels beside the app rather than as a
// reference: a net10 project cannot reference either build.
//
// The folder it sits in is the declaration of which platform it was built for. A payload found at the
// flat legacy path predates the split and is the Steam and GOG build, which is the only one that has
// ever shipped.
public static class CompanionPayload
{
    public const string SubFolder = "Companion";

    public static CompanionPayloadLookup Locate(string? installPlatformFolder = null) =>
        FindForPlatform(installPlatformFolder, [.. SearchRoots()]);

    // The platform is read off the install rather than passed in, because every caller has the install
    // path and none of them should have to know how a platform is decided. Locate() without it cannot
    // tell a matching payload from a mismatched one, so it never reports the caveat, which is the same
    // as not having one.
    public static CompanionPayloadLookup LocateForInstall(string? gameInstallPath) =>
        FindForInstall(gameInstallPath, [.. SearchRoots()]);

    // PlatformFolder, not FilterPlatformFolder: choosing which build to prefer is not a removal, and on
    // an install that holds both folders the historical default is the right guess to start from.
    public static CompanionPayloadLookup FindForInstall(string? gameInstallPath, params string[] roots) =>
        FindForPlatform(GameInstallLocator.DetectPlatform(gameInstallPath).PlatformFolder, roots);

    // Under a single-file publish the base directory is the extraction folder, not the folder the
    // exe sits in, so a payload placed beside the exe by hand would otherwise never be found. Both
    // are searched, and both are named when neither holds it.
    public static IReadOnlyList<string> SearchRoots()
    {
        var roots = new List<string> { AppContext.BaseDirectory };

        try
        {
            if (Environment.ProcessPath is { } process
                && System.IO.Path.GetDirectoryName(process) is { Length: > 0 } folder
                && !roots.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A base directory that is still readable is better than no search at all.
        }

        return roots;
    }

    public static CompanionPayloadLookup Find(params string[] roots) => FindForPlatform(null, roots);

    public static CompanionPayloadLookup FindForPlatform(string? installPlatformFolder, params string[] roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var looked = new List<string>();
        CompanionPayloadLookup? mismatched = null;

        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            foreach (var (candidate, platform) in Candidates(root))
            {
                looked.Add(candidate);

                if (!File.Exists(candidate))
                    continue;

                var matches = installPlatformFolder is null
                    || platform is null
                    || platform.Equals(installPlatformFolder, StringComparison.OrdinalIgnoreCase);

                if (matches)
                {
                    return new CompanionPayloadLookup(
                        candidate,
                        Strings.Current.Format("Core.DryRun.CompanionPayload.Found", candidate),
                        platform,
                        true);
                }

                // A build for the other platform is still offered rather than hidden, with the
                // mismatch stated: it may load, and refusing outright would take away the only dry run
                // a Game Pass user could have. It is only used when nothing better turns up.
                //
                // The wording is deliberately specific about what failure looks like. The companion is
                // loaded by the game, not by BEM, so a load failure is silent from here: the run ends
                // with nothing reported rather than with an error, and a caveat that did not say so
                // would leave the user to guess why an apparently clean run found nothing.
                mismatched ??= new CompanionPayloadLookup(
                    candidate,
                    Strings.Current.Format(
                        "Core.DryRun.CompanionPayload.Mismatched",
                        candidate,
                        PlatformDetection.DescribePlatform(platform),
                        PlatformDetection.DescribePlatform(installPlatformFolder)),
                    platform,
                    false);
            }
        }

        if (mismatched is not null)
            return mismatched;

        // "Found nothing" and "could not look" mean opposite things to the reader, so the places
        // that were checked are named rather than summarized.
        return new CompanionPayloadLookup(
            null,
            looked.Count == 0
                ? Strings.Current["Core.DryRun.CompanionPayload.NowhereToLook"]
                : Strings.Current.Format("Core.DryRun.CompanionPayload.NotFound", string.Join(", ", looked)));
    }

    private static IEnumerable<(string Path, string? Platform)> Candidates(string root)
    {
        foreach (var platform in GameInstallLocator.BinaryFolders)
        {
            yield return (
                System.IO.Path.Combine(root, SubFolder, platform, CompanionManifest.AssemblyFileName),
                platform);
        }

        yield return (
            System.IO.Path.Combine(root, SubFolder, CompanionManifest.AssemblyFileName),
            GameInstallLocator.StandardBinaryFolder);

        yield return (
            System.IO.Path.Combine(root, CompanionManifest.AssemblyFileName),
            GameInstallLocator.StandardBinaryFolder);
    }
}
