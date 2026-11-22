using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Toolkit;

// Every ending is its own statement. "Refused", "failed", "already done" and "the installer said it
// worked but BEM still cannot find it" mean four different things to whoever reads the line, and
// collapsing them would be a message that overstates what happened.
public enum ToolkitOutcomeKind
{
    Installed,
    Removed,
    AlreadyPresent,
    AlreadyAbsent,
    Refused,
    Failed,
    NotVerified,
    Unavailable
}

public sealed record ToolkitOutcome(ToolkitOutcomeKind Kind, string Message);

// Nothing here runs without an explicit press, and nothing here reports success on an install BEM
// cannot then find for itself.
public sealed class ToolkitInstaller
{
    public static string NoWinGet => Strings.Current["Core.Toolkit.Installer.NoWinGet"];

    private readonly RunCommand run;
    private readonly Func<string, string?> locate;
    private readonly Func<string?> findWinGet;
    private readonly ToolkitLedger ledger;

    public ToolkitInstaller(RunCommand run, Func<string, string?> locate, Func<string?> findWinGet, ToolkitLedger ledger)
    {
        this.run = run;
        this.locate = locate;
        this.findWinGet = findWinGet;
        this.ledger = ledger;
    }

    public ToolCommand? InstallCommand(ExternalTool tool) => ToolkitCommands.Install(tool, findWinGet());

    public ToolCommand? UninstallCommand(ExternalTool tool) => ToolkitCommands.Uninstall(tool, findWinGet());

    public async Task<ToolkitOutcome> InstallAsync(ExternalTool tool, CancellationToken cancellationToken)
    {
        if (locate(tool.Id) is { } already)
        {
            return new ToolkitOutcome(
                ToolkitOutcomeKind.AlreadyPresent,
                Strings.Current.Format("Core.Toolkit.Installer.AlreadyPresent", tool.Name, already));
        }

        if (!tool.CanInstall)
        {
            return new ToolkitOutcome(
                ToolkitOutcomeKind.Unavailable,
                tool.NotInstallableReason
                    ?? Strings.Current.Format("Core.Toolkit.Installer.NotInstallable", tool.Name));
        }

        if (InstallCommand(tool) is not { } command)
            return new ToolkitOutcome(ToolkitOutcomeKind.Unavailable, NoWinGet);

        var outcome = await ExecuteAsync(command, cancellationToken);

        if (outcome.Result is ProcessResult.RefusedByUser)
        {
            return new ToolkitOutcome(
                ToolkitOutcomeKind.Refused,
                Strings.Current.Format("Core.Toolkit.Installer.InstallRefused", tool.Name));
        }

        if (outcome.Result is ProcessResult.CouldNotStart)
        {
            return new ToolkitOutcome(
                ToolkitOutcomeKind.Failed,
                Strings.Current.Format("Core.Toolkit.Installer.InstallCouldNotStart", Trim(outcome.Output)));
        }

        if (locate(tool.Id) is { } found)
        {
            ledger.Record(tool.Id);

            return outcome.ExitCode == 0
                ? new ToolkitOutcome(
                    ToolkitOutcomeKind.Installed,
                    Strings.Current.Format("Core.Toolkit.Installer.Installed", tool.Name, found))
                : new ToolkitOutcome(
                    ToolkitOutcomeKind.Installed,
                    Strings.Current.Format(
                        "Core.Toolkit.Installer.InstalledWithNonZeroExit",
                        outcome.ExitCode, tool.Name, found, Trim(outcome.Output)));
        }

        return outcome.ExitCode == 0
            ? new ToolkitOutcome(
                ToolkitOutcomeKind.NotVerified,
                Strings.Current.Format("Core.Toolkit.Installer.NotVerified", tool.Name, Trim(outcome.Output)))
            : new ToolkitOutcome(
                ToolkitOutcomeKind.Failed,
                Strings.Current.Format(
                    "Core.Toolkit.Installer.InstallFailed", outcome.ExitCode, Trim(outcome.Output)));
    }

    public async Task<ToolkitOutcome> UninstallAsync(ExternalTool tool, CancellationToken cancellationToken)
    {
        if (locate(tool.Id) is null)
        {
            return new ToolkitOutcome(
                ToolkitOutcomeKind.AlreadyAbsent,
                Strings.Current.Format("Core.Toolkit.Installer.AlreadyAbsent", tool.Name));
        }

        // The copy that was here first belongs to whoever put it here.
        if (!ledger.WasInstalledByBem(tool.Id))
        {
            return new ToolkitOutcome(
                ToolkitOutcomeKind.Unavailable,
                Strings.Current.Format("Core.Toolkit.Installer.NotBemInstalled", tool.Name));
        }

        if (UninstallCommand(tool) is not { } command)
            return new ToolkitOutcome(ToolkitOutcomeKind.Unavailable, NoWinGet);

        var outcome = await ExecuteAsync(command, cancellationToken);

        if (outcome.Result is ProcessResult.RefusedByUser)
        {
            return new ToolkitOutcome(
                ToolkitOutcomeKind.Refused,
                Strings.Current.Format("Core.Toolkit.Installer.RemoveRefused", tool.Name));
        }

        if (outcome.Result is ProcessResult.CouldNotStart)
        {
            return new ToolkitOutcome(
                ToolkitOutcomeKind.Failed,
                Strings.Current.Format("Core.Toolkit.Installer.RemoveCouldNotStart", Trim(outcome.Output)));
        }

        if (locate(tool.Id) is { } stillThere)
        {
            return new ToolkitOutcome(
                outcome.ExitCode == 0 ? ToolkitOutcomeKind.NotVerified : ToolkitOutcomeKind.Failed,
                Strings.Current.Format(
                    "Core.Toolkit.Installer.StillPresent", tool.Name, stillThere, outcome.ExitCode, Trim(outcome.Output)));
        }

        ledger.Forget(tool.Id);

        return new ToolkitOutcome(
            ToolkitOutcomeKind.Removed, Strings.Current.Format("Core.Toolkit.Installer.Removed", tool.Name));
    }

    private async Task<ProcessOutcome> ExecuteAsync(ToolCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return await run(command, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return new ProcessOutcome(ProcessResult.CouldNotStart, -1, ex.Message);
        }
    }

    private static string Trim(string output)
    {
        var trimmed = output.Trim();

        return trimmed.Length == 0 ? Strings.Current["Core.Toolkit.Installer.NoOutput"] : trimmed;
    }
}
