namespace BannerlordEnvironmentManager.Core.Instances;

// The headless entry behind `--repair-only`: runs the same StartupRepair a normal start runs, with no
// window, and says whether the canonical folders are plain folders afterwards. A test harness whose BEM
// is gone (killed, crashed) calls it instead of reimplementing the repair, so the one repair path is the
// only code that ever touches the owner's four folders.
public static class RepairOnlyRun
{
    public const string Switch = "--repair-only";

    public static bool IsRequested(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Any(arg => string.Equals(arg, Switch, StringComparison.OrdinalIgnoreCase));
    }

    // Exit code 0 when no canonical folder is a junction when this ends, 1 otherwise. A repair that
    // was refused (a launch holds the lock) or failed on a path is 1 as well, since the folders are
    // then not known to be back.
    public static int Run(CanonicalPathSet paths, InstanceManager manager, Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(say);

        var problems = 0;

        if (!manager.CanRepairOnStartup)
        {
            say("repair: no resting instance is recorded, so there is nothing to put back from");
        }
        else
        {
            var report = manager.RepairOnStartup();

            foreach (var path in report.Repaired)
                say($"repaired={path}");

            foreach (var path in report.Failed)
            {
                say($"failed={path}");
                problems++;
            }

            if (report.WasRefused)
            {
                say($"refused={report.Describe()}");
                problems++;
            }
        }

        foreach (var pair in paths.Pairs)
        {
            if (!JunctionManager.IsJunction(pair.LivePath))
                continue;

            say($"junction-left={pair.LivePath}");
            problems++;
        }

        say(problems == 0 ? "clean" : "not-clean");

        return problems == 0 ? 0 : 1;
    }
}
