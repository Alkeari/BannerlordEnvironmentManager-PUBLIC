namespace BannerlordEnvironmentManager.Core.Install;

public enum InstallTargetOutcome
{
    // Files were written into this instance.
    Installed,

    // The archive had nothing for this instance: no loose file that matched anything under its
    // Modules folder. Reported apart from Installed because nothing changed here and saying it
    // succeeded would read as the mod being present.
    NothingToDo,

    // Nothing was written into this instance. The rest were still installed into.
    Failed
}

public sealed record InstallTargetResult(
    InstallTarget Target,
    InstallTargetOutcome Outcome,
    int FilesWritten = 0,
    string? Error = null);

// What one archive did across every instance it was sent to, instance by instance. A run that names
// only a total leaves the user to open four versions and look, which is the work the whole feature
// exists to save them.
public sealed record MultiInstanceInstallReport(IReadOnlyList<InstallTargetResult> Targets)
{
    public int Installed => Targets.Count(target => target.Outcome == InstallTargetOutcome.Installed);

    public int FilesWritten => Targets.Sum(target => target.FilesWritten);

    public bool Failed => Targets.Any(target => target.Outcome == InstallTargetOutcome.Failed);

    public IReadOnlyList<InstallTargetResult> Failures =>
        [.. Targets.Where(target => target.Outcome == InstallTargetOutcome.Failed)];
}
