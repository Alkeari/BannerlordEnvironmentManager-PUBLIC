using System;
using System.Globalization;

namespace BannerlordEnvironmentManager.Companion;

internal enum CrashGuardAction
{
    // Written down and passed on: the game crashes exactly as it would have.
    RecordOnly,

    // Held back for the few ticks an emergency save needs, after which the game closes.
    SaveThenClose
}

// The decisions, with nothing from the game in them, so the tests can compile this file directly.
internal static class CrashGuardPolicy
{
    public const string SaveNamePrefix = "BEM Emergency ";

    public const int MaxNameAttempts = 100;

    // A campaign that exists is not a campaign that can be saved: Campaign.Current is set while the game loads,
    // before the main hero exists, and a save then has nothing to hold. Only a started campaign, in a state
    // that is not loading, character creation or a menu outside the campaign, is playable.
    public static bool IsPlayableCampaign(bool campaignExists, bool gameExists, bool mainHeroExists, string? activeStateName)
    {
        if (!campaignExists || !gameExists || !mainHeroExists)
            return false;

        if (string.IsNullOrEmpty(activeStateName))
            return false;

        foreach (var blocked in new[] { "Loading", "CharacterCreation", "Initial", "MainMenu", "Lobby", "Start" })
            if (activeStateName!.IndexOf(blocked, StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

        return true;
    }

    // Continuing past a crash is only worth it for a save, and a save is only possible on the campaign
    // outside a mission. Ironman keeps its single slot, so it never gets a second file.
    // What makes two crashes "the same" for Continue: the innermost exception's type and the frame it
    // was thrown from. A broken screen the player continued past throws the same thing every tick, and
    // he asked that pressing Continue moves past every repeat of it silently.
    public static string Signature(string innermostType, string? innermostStack)
    {
        var frame = string.Empty;
        foreach (var line in (innermostStack ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("at ", StringComparison.Ordinal))
            {
                frame = trimmed;
                break;
            }
        }

        return innermostType + "|" + frame;
    }

    // His three forms: continue and save, save only, neither. A mod built against another game version
    // fails the same call every time it runs, so continuing only repeats the crash; the game around it
    // is still sound, so it can still be saved.
    private static readonly string[] BinaryMismatch =
    {
        "System.MissingMethodException", "System.MissingFieldException", "System.MissingMemberException",
        "System.TypeLoadException", "System.EntryPointNotFoundException", "System.BadImageFormatException"
    };

    // A missing or unloadable file blocks Continue only when it is a program file (an assembly the mod
    // needs); a mod missing an ordinary file, such as its settings, may well carry on.
    private static readonly string[] AssemblyFileErrors = { "System.IO.FileNotFoundException", "System.IO.FileLoadException" };

    public static bool CanContinue(string? innermostType, string? innermostMessage = null)
    {
        var type = innermostType ?? string.Empty;
        if (Array.IndexOf(BinaryMismatch, type) >= 0)
            return false;

        return Array.IndexOf(AssemblyFileErrors, type) < 0
            || (innermostMessage ?? string.Empty).IndexOf("file or assembly", StringComparison.OrdinalIgnoreCase) < 0;
    }

    // A game in an install whose Companion a BEM on a data folder of its own (--bem-data-root, an agent's
    // instance) deployed has no player at the window: the crash is recorded and the policy decides,
    // nothing waits. The signal is a marker file in the Companion module folder of that install, never the
    // process environment, which a shared Steam client hands on to launches it did not come from. The
    // marker names the agent data root; a missing, unreadable, relative or no longer existing root asks.
    public const string UnattendedMarkerFileName = "unattended.marker";

    public static bool AsksPlayer(string? markerContent)
    {
        var root = (markerContent ?? string.Empty).Trim();
        try
        {
            return root.Length == 0 || !System.IO.Path.IsPathRooted(root) || !System.IO.Directory.Exists(root);
        }
        catch
        {
            return true;
        }
    }

    public static CrashGuardAction Decide(bool alreadyCaught, bool inCampaign, bool inMission, bool ironman, bool saveLimitReached) =>
        !alreadyCaught && inCampaign && !inMission && !ironman && !saveLimitReached
            ? CrashGuardAction.SaveThenClose
            : CrashGuardAction.RecordOnly;

    // One slot, overwritten each time, so a crash that ends the game outright (a native fault, where no
    // emergency save is possible) still leaves a save a few minutes old without filling the save list.
    public const string RollingSaveName = "BEM Rolling Save";

    public static readonly TimeSpan RollingSaveInterval = TimeSpan.FromMinutes(10);

    // Written only on the campaign map, never in a mission or Ironman, never beside another save, never
    // after a crash was caught (that state is what the emergency save is for), and only as a new file
    // when the save limit leaves room: overwriting its own slot never adds a file.
    public static bool ShouldRollingSave(
        bool caught, bool savePending, bool rollingInFlight, bool onMap, bool inMission, bool ironman,
        bool saveLimitReached, bool rollingExists, DateTime? lastRollingUtc, DateTime nowUtc) =>
        !caught && !savePending && !rollingInFlight && onMap && !inMission && !ironman
        && (rollingExists || !saveLimitReached)
        && (lastRollingUtc is null || nowUtc - lastRollingUtc.Value >= RollingSaveInterval);

    // Before a risky transition (a state push: a mission, a battle, a menu leaving the map) the map state
    // is saved ahead of the interval, but never twice within a minute, so a fast chain of pushes costs one.
    public static readonly TimeSpan TransitionSaveSpacing = TimeSpan.FromMinutes(1);

    public static bool ShouldSaveBeforeTransition(
        bool caught, bool savePending, bool rollingInFlight, bool onMap, bool inMission, bool ironman,
        bool saveLimitReached, bool rollingExists, DateTime? lastRollingUtc, DateTime nowUtc) =>
        !caught && !savePending && !rollingInFlight && onMap && !inMission && !ironman
        && (rollingExists || !saveLimitReached)
        && (lastRollingUtc is null || nowUtc - lastRollingUtc.Value >= TransitionSaveSpacing);

    // The game's save call overwrites a name that exists, so the name is proven free first. Null means
    // none was free, and then no save is written at all.
    public static string? UniqueSaveName(DateTime localNow, Func<string, bool> exists)
    {
        var stem = SaveNamePrefix + localNow.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);

        if (!exists(stem))
            return stem;

        for (var i = 2; i <= MaxNameAttempts; i++)
        {
            var candidate = stem + " " + i.ToString(CultureInfo.InvariantCulture);

            if (!exists(candidate))
                return candidate;
        }

        return null;
    }
}
