using BannerlordEnvironmentManager.Core.DryRun;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Settings;

namespace BannerlordEnvironmentManager.Core.Launcher;

public enum LaunchWatchState
{
    // No watch is armed. Nothing was added to this launch and nothing about it differs from any other.
    NotArmed,

    // The companion is on this run's module list, for this run only.
    Watched,

    // A watch is armed, but the chosen target reads LauncherData.xml rather than a module list, and
    // BEM will not write its own module into the saved load order to reach it.
    TargetTakesNoModuleList,

    // A watch is armed and the target does take a module list, but the watched command line could not
    // be built. Reported rather than swallowed: a run the user believes is watched and is not is the
    // failure this whole feature exists to avoid.
    CouldNotBuild,

    // A watch is armed and the companion would fit on the module list, but the command line carrying
    // it would not fit in the buffer the game copies it into. Sending it anyway kills the game two
    // seconds in with nothing written anywhere, so the run goes ahead unwatched instead.
    WouldNotFitTheCommandLine
}

// WatchSessionId is the id of the armed watch, not of this launch. The companion derives a run id of
// its own per process from it, so one armed watch produces one trail per launch and the Watch page
// finds them all by reading the folder.
public sealed record LaunchPlan(LaunchTarget? Target, LaunchWatchState Watch, string WatchSessionId, string Note)
{
    public bool IsWatched => Watch == LaunchWatchState.Watched;
}

public sealed record LaunchPlanRequest(
    string GameInstallPath,
    IReadOnlyList<ModuleEntry> EnabledModules,
    LaunchTargetKind? PreferredTarget = null,
    string ExtraArguments = "",
    BlseCrashHandlerOptions? CrashHandling = null,
    bool WatchArmed = false,
    string WatchSessionId = "");

// What one press of Launch actually starts, watch included. The watch is decided here, with the
// target, rather than on a launch path of its own: it changes this run's command line and nothing
// else, and every other launch path in BEM already goes through LaunchTargetResolver.
//
// The saved load order is never touched by any of this. The companion is inserted into the module
// list that goes on the command line for one run, which is what BLSE and Bannerlord.exe read, and
// LauncherData.xml is not.
public static class WatchedLaunch
{
    // Only these two carry a module list on their command line, and the companion has to be in that
    // list to load at all. Everything else reads LauncherData.xml instead.
    public static bool CarriesModuleList(LaunchTargetKind kind) =>
        kind is LaunchTargetKind.BlseStandalone or LaunchTargetKind.GameExecutable;

    public static bool CanBeWatched(string gameInstallPath) =>
        LaunchTargetResolver.FindAvailable(gameInstallPath).Any(target => CarriesModuleList(target.Kind));

    public static IReadOnlyList<ModuleEntry> WithCompanion(IReadOnlyList<ModuleEntry> enabled) =>
        WithCompanion(enabled, CompanionManifest.ModuleId);

    // Position 2, immediately after Bannerlord.Harmony, so the user's real Harmony wins the assembly
    // load race. Loading first would mean watching a configuration the user never plays.
    //
    // companionId is the id of the companion that is actually on disk, which is not always the current
    // one: a watch armed by a build of BEM from before the rename installed the module under its old
    // name, and putting the new id on the command line would find nothing there and watch nothing.
    //
    // Every companion entry already in the list comes out first. The user's saved order can contain
    // BEM's own module, because until now it appeared in the Environment list looking like a mod that
    // needed enabling, and adding it again would put the same id on one command line twice.
    public static IReadOnlyList<ModuleEntry> WithCompanion(IReadOnlyList<ModuleEntry> enabled, string companionId)
    {
        ArgumentNullException.ThrowIfNull(enabled);

        var companion = new ModuleEntry(new ModuleId(companionId), null, IsEnabled: true);
        var ordered = new List<ModuleEntry>(enabled.Count + 1);

        foreach (var entry in enabled)
        {
            if (CompanionManifest.IsCompanionId(entry.Id.Value))
                continue;

            ordered.Add(entry);

            if (string.Equals(entry.Id.Value, CompanionManifest.HarmonyModuleId, StringComparison.OrdinalIgnoreCase))
                ordered.Add(companion);
        }

        return ordered;
    }

    // Everything a watched launch costs on the game's command line, and the whole of it: the separator
    // and the companion's module id, appended to a list the launch was sending anyway.
    //
    // It cannot be zero. The companion has to be a module the engine loaded to run at all: the engine
    // resolves modules only from the _MODULES_ list, and BLSE's own extension points are gated behind
    // TypeFinder.GetInterceptorTypes, which intersects the loaded assemblies with the SubModule DLL
    // paths of the modules on that same list. There is no supported way in either to load an assembly
    // at startup without naming a module, so one separator and the shortest usable id is the floor.
    //
    // Stated for the id BEM installs today. A companion still on disk under an older, longer name
    // costs that name's length instead, until the next time watching is armed and replaces it.
    public static int CompanionCharacterCost => CompanionManifest.ModuleId.Length + 1;

    public static LaunchPlan Plan(LaunchPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plain = LaunchTargetResolver.Resolve(
            request.GameInstallPath,
            request.PreferredTarget,
            request.EnabledModules,
            request.ExtraArguments,
            request.CrashHandling);

        if (plain is null || !request.WatchArmed)
            return new LaunchPlan(plain, LaunchWatchState.NotArmed, string.Empty, DescribePlain(plain));

        if (!CarriesModuleList(plain.Kind))
        {
            return new LaunchPlan(
                plain,
                LaunchWatchState.TargetTakesNoModuleList,
                string.Empty,
                Strings.Current.Format("Core.Launcher.Watch.TargetTakesNoModuleList", plain.DisplayName));
        }

        // Whichever name the companion is installed under, rather than the current one: a folder left
        // by an older build of BEM still loads, and a launch that names a module the game cannot
        // resolve would watch nothing and say it was watching.
        var companionId = new CompanionInstaller(request.GameInstallPath).InstalledModuleId
            ?? CompanionManifest.ModuleId;

        // The arguments are the plain launch's, unchanged. Nothing marks this run as watched, because
        // nothing needs to: the companion reads watch-session.json, which is the same file that armed
        // it, so an armed watch costs the module id and not one character more.
        var watched = LaunchTargetResolver.Resolve(
            request.GameInstallPath,
            plain.Kind,
            WithCompanion(request.EnabledModules, companionId),
            request.ExtraArguments,
            request.CrashHandling);

        // Resolve falls back to whatever it can find when the preferred kind will not build, so a
        // target of a different kind here is not the watched launch that was asked for.
        if (watched is null || watched.Kind != plain.Kind)
        {
            return new LaunchPlan(
                plain,
                LaunchWatchState.CouldNotBuild,
                string.Empty,
                Strings.Current.Format("Core.Launcher.Watch.CouldNotBuild", plain.DisplayName));
        }

        // The last thing checked and the one that decides whether the companion goes at all. Watching
        // now costs only the companion's module id, but a load order sitting on the game's limit has
        // no room for even that, and sending it anyway kills the game two seconds in leaving nothing
        // behind. Launching plain is not a smaller feature here: the watched launch was never going
        // to run.
        if (GameCommandLine.Measure(watched) is { Fits: false } budget)
        {
            return new LaunchPlan(
                plain,
                LaunchWatchState.WouldNotFitTheCommandLine,
                string.Empty,
                Strings.Current.Plural(
                    "Core.Launcher.Watch.WouldNotFitTheCommandLine",
                    budget.Overflow,
                    CompanionCharacterCost,
                    GameCommandLine.Limit,
                    DescribeBudget(GameCommandLine.Measure(plain))));
        }

        var session = request.WatchSessionId;

        return new LaunchPlan(
            watched,
            LaunchWatchState.Watched,
            session,
            (session.Length == 0
                ? Strings.Current["Core.Launcher.Watch.PlainWatched"]
                : Strings.Current.Format("Core.Launcher.Watch.SessionWatched", session))
            + Strings.Current["Core.Launcher.Watch.WatchedSuffix"]);
    }

    // Said about an ordinary launch, not only a watched one. The limit belongs to the game and BEM's
    // companion is only ever the last few characters of it: an install that is one module away from
    // the cliff is one module away whether BEM is watching or not, and the user cannot see that
    // anywhere else.
    //
    // Silent while there is real room, which is the state of every install that is not near the
    // limit. Anything else would put a line about character counts in front of every launch.
    private static string DescribePlain(LaunchTarget? plain)
    {
        if (plain is null)
            return string.Empty;

        var budget = GameCommandLine.Measure(plain);

        if (budget.Fits && budget.Headroom > WarnBelowHeadroom)
            return string.Empty;

        return DescribeBudget(budget);
    }

    // Enough for one more module of an ordinary name, which is the decision the number has to inform.
    private const int WarnBelowHeadroom = 40;

    private static string DescribeBudget(CommandLineBudget budget) => budget.Fits
        ? Strings.Current.Format(
            "Core.Launcher.Watch.Budget.Fits", budget.Length, budget.Limit, budget.Headroom)
        : Strings.Current.Format(
            "Core.Launcher.Watch.Budget.Overflow", budget.Length, budget.Limit, budget.Overflow);
}
