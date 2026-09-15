using BannerlordEnvironmentManager.Core.Modules;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// The two game versions a registry check compares are read from different places and do not carry the
// same number of components. The captured side comes from the companion inside the running game, which
// calls GetApplicationVersionWithBuildNumber() and returns four parts including the build, "v1.4.8.119303".
// The current side comes from GameVersionReader reading Native's SubModule.xml, which holds three,
// "v1.4.8". Compared as strings those can never be equal, so every registry was stale the instant it was
// written, for every user, forever.
//
// The rule is not invented here. ModuleVersion.CompareTo already orders game versions on major, minor and
// revision and ignores the changeset, because the changeset is TaleWorlds' internal build counter and no
// manifest anywhere carries it. This follows that same ordering, so a build-number difference inside one
// major.minor.revision is not a reason to distrust a registry.
public static class GameVersionMatch
{
    public static bool SameGameVersion(string? captured, string? current)
    {
        // A side that was never recorded is not evidence of a change. Both existing call sites already
        // skipped the comparison in that case, and the rule keeps that behavior in one place.
        if (string.IsNullOrWhiteSpace(captured) || string.IsNullOrWhiteSpace(current))
            return true;

        if (string.Equals(captured, current, StringComparison.OrdinalIgnoreCase))
            return true;

        // Text that will not parse is not waved through. Comparing it as it arrived is the honest
        // fallback: BEM cannot tell that two strings it does not understand mean the same build.
        if (!ModuleVersion.TryParse(captured, out var left) || !ModuleVersion.TryParse(current, out var right))
            return false;

        // Type is part of the identity: a beta branch build and a release build sharing 1.4.8 are
        // genuinely different games, and only the changeset is being ignored.
        return left.Type == right.Type
            && left.Major == right.Major
            && left.Minor == right.Minor
            && left.Revision == right.Revision;
    }
}
