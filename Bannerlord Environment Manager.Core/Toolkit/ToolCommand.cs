namespace BannerlordEnvironmentManager.Core.Toolkit;

// Shown before it is run, in the form it will be run, so a command can be read and declined rather
// than trusted.
public sealed record ToolCommand(string Executable, string Arguments)
{
    public string DisplayText =>
        Executable.Contains(' ', StringComparison.Ordinal)
            ? $"\"{Executable}\" {Arguments}"
            : $"{Executable} {Arguments}";
}

public enum ProcessResult
{
    Completed,
    RefusedByUser,
    CouldNotStart
}

public sealed record ProcessOutcome(ProcessResult Result, int ExitCode, string Output);

public delegate Task<ProcessOutcome> RunCommand(ToolCommand command, CancellationToken cancellationToken);

// Every switch here was read out of this machine's own winget install --help and uninstall --help
// rather than remembered.
public static class ToolkitCommands
{
    // --exact so a partial id match cannot install a different package, and --disable-interactivity
    // because a prompt behind a hidden window would wait forever with nobody able to answer it.
    public static ToolCommand? Install(ExternalTool tool, string? winGetPath) =>
        tool.WinGetId is { } id && !string.IsNullOrWhiteSpace(winGetPath)
            ? new ToolCommand(
                winGetPath,
                $"install --id {id} --exact --source winget --accept-package-agreements "
                    + "--accept-source-agreements --disable-interactivity")
            : null;

    public static ToolCommand? Uninstall(ExternalTool tool, string? winGetPath) =>
        tool.WinGetId is { } id && !string.IsNullOrWhiteSpace(winGetPath)
            ? new ToolCommand(
                winGetPath,
                $"uninstall --id {id} --exact --accept-source-agreements --disable-interactivity")
            : null;
}
