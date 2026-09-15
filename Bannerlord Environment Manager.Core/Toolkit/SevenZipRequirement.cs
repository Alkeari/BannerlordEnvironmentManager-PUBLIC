using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Toolkit;

// Where the search for 7-Zip ended and what to say about it. Null Path means BEM still cannot open the
// archive, and Message is then the whole explanation rather than a summary of one.
public sealed record SevenZipProvision(string? Path, string Message)
{
    public bool Found => Path is not null;
}

// The one thing BEM says when an archive needs 7-Zip and 7-Zip is not there. The installer and the
// safety scanner both reach this moment, and two wordings sent the same user to two different places:
// the error said "Optional tools" while the README said "Toolkit", and neither was visible in Basic
// mode. One name, one place, one escape hatch.
public static class SevenZipRequirement
{
    // The name of the place, written once. Every string below and the Settings section itself use it,
    // so renaming the section renames the instruction the user is given.
    public static string Place => Strings.Current["Core.Toolkit.SevenZip.Place"];

    public const string DownloadPage = "https://www.7-zip.org/";

    // 7ZIP_EXE is only for a copy that is not where BEM already looks: SevenZipLocator probes Program
    // Files and Program Files (x86) first and reads the variable last. It has to name 7z.exe itself
    // because BEM runs "7z x" against it, and a process reads its environment once when it starts, so a
    // variable set while BEM is running is not seen until BEM is started again.
    public static string ManualHelp =>
        Strings.Current.Format("Core.Toolkit.SevenZip.ManualHelp", Place, DownloadPage);

    public static string NotInstalled =>
        Strings.Current.Format("Core.Toolkit.SevenZip.NotInstalled", ManualHelp);

    public static string CouldNotOpen =>
        Strings.Current.Format("Core.Toolkit.SevenZip.CouldNotOpen", ManualHelp);

    // Off by default, so this only ever installs anything on a machine whose owner ticked the box in
    // Settings first. With it off the answer is the same sentence the user would have seen anyway.
    public static async Task<SevenZipProvision> EnsureAsync(
        Func<string?> locate,
        bool installOnDemand,
        Func<CancellationToken, Task<ToolkitOutcome>> install,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(locate);
        ArgumentNullException.ThrowIfNull(install);

        if (locate() is { } already)
            return new SevenZipProvision(already, string.Empty);

        if (!installOnDemand)
            return new SevenZipProvision(null, NotInstalled);

        var outcome = await install(cancellationToken);

        // Located again rather than trusted: an installer that reports success over a copy BEM cannot
        // then find has not made the archive openable, and saying so is the difference between a plan
        // that worked and one that only claimed to.
        return locate() is { } installed
            ? new SevenZipProvision(
                installed,
                Strings.Current.Format("Core.Toolkit.SevenZip.InstalledNow", outcome.Message))
            : new SevenZipProvision(
                null,
                Strings.Current.Format("Core.Toolkit.SevenZip.CouldNotInstall", outcome.Message, ManualHelp));
    }
}
