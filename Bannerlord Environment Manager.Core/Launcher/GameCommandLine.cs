using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Launcher;

public sealed record CommandLineBudget(int Length, int Limit, string Suffix)
{
    public bool Fits => Length <= Limit;

    public int Headroom => Limit - Length;

    public int Overflow => Length - Limit;

    // Near and over are separate states: an order that has already overflowed is not approaching
    // anything, and the load order screen says a different thing about each.
    public bool IsNearTheLimit => Fits && Headroom < GameCommandLine.NearTheLimit;

    // The load order screen is somewhere the user looks constantly, so this is the terse form. The
    // sentence in Describe stays for the preflight, which they opened on purpose.
    public string Summary => Fits
        ? Strings.Current.Format("Core.Launcher.CommandLine.Summary.Fits", Length, Limit, Headroom)
        : Strings.Current.Format("Core.Launcher.CommandLine.Summary.Overflow", Length, Limit, Overflow);

    // Stated in characters rather than in modules, because it is characters the game counts: two
    // module ids of the same name length cost the same and a rename costs nothing.
    public string Describe() => Fits
        ? Strings.Current.Format("Core.Launcher.CommandLine.Describe.Fits", Length, Limit, Headroom)
        : Strings.Current.Format("Core.Launcher.CommandLine.Describe.Overflow", Length, Limit, Overflow);
}

// How much of the game's own command line the launch would use, and whether it fits.
//
// This is measured, not estimated. Bannerlord's native startup copies the whole argument string into
// a 4096-byte stack buffer with strcpy_s. In the installed build that call site is
// TaleWorlds.Native.dll+0x45e83:
//
//     mov  r8, r14                 ; the argument string
//     mov  edx, 0x1000             ; the destination is 4096 bytes
//     lea  rcx, [rbp+0x8b8]
//     call strcpy_s
//
// A string that does not fit is not truncated. strcpy_s calls the UCRT invalid parameter handler,
// which calls __fastfail(FAST_FAIL_INVALID_ARG), and Windows ends the process with 0xC0000409 about
// two seconds in. That happens inside the native entry point, before the CLR has loaded one game
// assembly, so it leaves no crash report, no rgl log, no BUTR report and no BEM breadcrumb. Nothing
// managed ever runs to write one. The only trace is an Application Error event naming ucrtbase.dll.
//
// Read out of the crash dump of a real launch: the string the game refused was 4103 characters, and
// the 240-module order that plays fine on the same install is 4053. Nine characters of headroom on a
// healthy install is why this is counted rather than assumed, and why anything BEM adds to a launch
// of its own has to be measured before it is sent.
//
// Disproof condition: a launch whose measured length is at or under the limit that still dies with
// 0xC0000409 in under five seconds, or one over the limit that reaches the main menu. Either would
// mean the buffer is not the one this reads.
public static class GameCommandLine
{
    // 4096 bytes of destination, one of which is the terminator strcpy_s has to write.
    public const int Limit = 4095;

    // Below this the next module enabled is plausibly the one that kills the launch: one enabled entry
    // on the reference install averages 17 characters including its separator, so 20 is about one more
    // module and no more. Calibrated against recorded launches rather than picked: the 240-module order
    // measuring 4053 with 42 to spare says nothing, and the 242-module order measuring 4086 with 9 to
    // spare does. It lives here so the load order screen and the preflight agree on what "close" means.
    public const int NearTheLimit = 20;

    // Appended by BLSE, not by the game: the literal lives in Bannerlord.BLSE.Shared.dll and arrives
    // in the string TaleWorlds.Native copies. It is 12 characters that BEM never sees in the
    // arguments it builds, and leaving them out would put the limit 12 characters too high on
    // exactly the launches that are closest to it.
    public const string BlseSuffix = " no_watchdog";

    public static CommandLineBudget Measure(string? arguments, LaunchTargetKind kind)
    {
        var suffix = AppendsWatchdogFlag(kind) ? BlseSuffix : string.Empty;

        return new CommandLineBudget((arguments ?? string.Empty).Length + suffix.Length, Limit, suffix);
    }

    public static CommandLineBudget Measure(LaunchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return Measure(target.Arguments, target.Kind);
    }

    // Only the target that hands its arguments to the game itself. The launcher shims start a
    // launcher window instead, which builds its own command line out of LauncherData.xml, and
    // Bannerlord.exe has no BLSE in it to append anything.
    private static bool AppendsWatchdogFlag(LaunchTargetKind kind) => kind == LaunchTargetKind.BlseStandalone;
}
