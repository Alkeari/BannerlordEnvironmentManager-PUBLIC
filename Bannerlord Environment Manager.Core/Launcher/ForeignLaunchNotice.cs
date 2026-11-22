using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Launcher;

// What BEM tells the owner when the game was started by something other than BEM (Vortex, MO2,
// Novus, Steam): the same preflight BEM's own Launch runs, reduced to one line a tray notification
// carries. Null when the preflight found nothing that gates a launch, so a clean start is silent.
public static class ForeignLaunchNotice
{
    public static string? For(PreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var prominent = report.Prominent;

        if (prominent.Count == 0)
            return null;

        var critical = prominent.Count(f => f.Grade == PreflightGrade.Critical);
        var warnings = prominent.Count - critical;
        var parts = new List<string>();

        if (critical > 0)
            parts.Add(Strings.Current.Plural("Core.Launcher.Foreign.Critical", critical));

        if (warnings > 0)
            parts.Add(Strings.Current.Plural("Core.Launcher.Foreign.Warning", warnings));

        return Strings.Current.Format("Core.Launcher.Foreign.Notice", string.Join(", ", parts));
    }
}
