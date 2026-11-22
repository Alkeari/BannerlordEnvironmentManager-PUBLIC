namespace BannerlordEnvironmentManager.Core.Launcher;

// The game's own switch for its developer cheats. BEM keeps it as a setting of its own, off by
// default, rather than asking the player to type it into the extra arguments.
public static class CheatModeArgument
{
    public const string Switch = "/cheat_mode";

    // The arguments a launch carries: what the player typed, plus the switch when the setting is on.
    // Off never takes the switch out of what the player typed themselves, and on never adds a second.
    public static string Apply(string? extraArguments, bool cheatMode)
    {
        var typed = (extraArguments ?? string.Empty).Trim();

        if (!cheatMode || IsIn(typed))
            return typed;

        return typed.Length == 0 ? Switch : typed + " " + Switch;
    }

    public static bool IsIn(string? arguments) =>
        (arguments ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(argument => string.Equals(argument, Switch, StringComparison.OrdinalIgnoreCase));
}
