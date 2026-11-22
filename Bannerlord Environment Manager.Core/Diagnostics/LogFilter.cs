using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// Almost nobody opens a diagnostics tab to read a log that reported nothing, so the list leaves those
// out until asked. A log BEM could not read is not one of them: nothing established that it is clean,
// and hiding it would be a claim the scan never made.
public static class LogFilter
{
    public static bool HasFindings(LogScan scan) => scan.Error is not null || scan.MatchingLines > 0;

    public static string Describe(int withFindings, int total) => total == 0
        ? Strings.Current["Core.Diagnostics.LogFilter.None"]
        : Strings.Current.Plural("Core.Diagnostics.LogFilter.Count", total, withFindings)
          + Strings.Current.Plural("Core.Diagnostics.LogFilter.Verb", withFindings);
}
