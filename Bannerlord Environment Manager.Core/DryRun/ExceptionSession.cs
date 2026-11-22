using System.Text.Json;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Core.DryRun;

// One launch BEM started with the Bannerlord Exception Manager on. The property names are a contract with
// CompanionGate in the Companion, which reads this file by hand.
public sealed record ExceptionSessionState(string RunId, string GameInstallPath, string BemPath, DateTime ArmedUtc)
{
    public bool IsEmpty => RunId.Length == 0;

    public static ExceptionSessionState None => new(string.Empty, string.Empty, string.Empty, default);
}

public sealed record ExceptionArmResult(bool Armed, string RunId, string Reason)
{
    public static ExceptionArmResult Refused(string reason) => new(false, string.Empty, reason);
}

// Arms the crash catcher for one launch, without a watch. A watch records a whole session's timeline and
// stays armed until the owner stops it; this only lets the Companion catch a crash, and it lapses by
// itself: the Companion honors the file for FreshMinutes after ArmedUtc and is inert after that, so a
// launch from Steam next week with the folder still on disk does nothing.
public sealed class ExceptionSession(string stateFilePath)
{
    // A contract with CompanionGate.GuardFreshMinutes. Long enough for the slowest modded start to reach
    // the Companion's constructor, short enough that the file never arms a launch BEM did not start.
    public const int FreshMinutes = 10;

    public static string DefaultStateFilePath => Path.Combine(AppDataRoot.Folder, "exception-session.json");

    public string StateFilePath { get; } = stateFilePath;

    public ExceptionSessionState Read()
    {
        try
        {
            if (!File.Exists(StateFilePath))
                return ExceptionSessionState.None;

            return JsonSerializer.Deserialize<ExceptionSessionState>(File.ReadAllText(StateFilePath))
                ?? ExceptionSessionState.None;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return ExceptionSessionState.None;
        }
    }

    public bool IsFresh(DateTime utcNow)
    {
        var state = Read();

        return !state.IsEmpty && utcNow >= state.ArmedUtc && utcNow - state.ArmedUtc <= TimeSpan.FromMinutes(FreshMinutes);
    }

    // Puts the Companion in place when it is not already there under its current name, and writes the
    // file. bemPath is the exe the Companion starts to show the window; empty means it shows none and the
    // guard behaves as it did before the window existed.
    public ExceptionArmResult Arm(string gameInstallPath, string payloadPath, string bemPath, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameInstallPath);
        ArgumentNullException.ThrowIfNull(payloadPath);
        ArgumentNullException.ThrowIfNull(bemPath);

        var installer = new CompanionInstaller(gameInstallPath);

        // Reinstalled whenever what is on disk differs from what this build would write, not only when
        // it is missing: a folder an older build left under the current name kept its old manifest and
        // assembly for ever, because it counted as installed.
        if (!installer.IsCurrent(payloadPath))
        {
            var install = installer.Install(payloadPath);

            if (!install.Installed)
            {
                // A stale copy that cannot be replaced (no payload to copy) still beats none.
                if (!installer.IsInstalled || installer.IsUnderLegacyName)
                    return ExceptionArmResult.Refused(install.Reason ?? string.Empty);
            }
        }

        var runId = DryRunPaths.NewRunId();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);

            var temp = StateFilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new ExceptionSessionState(runId, gameInstallPath, bemPath, utcNow)));
            File.Move(temp, StateFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ExceptionArmResult.Refused(ex.Message);
        }

        return new ExceptionArmResult(true, runId, string.Empty);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(StateFilePath))
                File.Delete(StateFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It lapses by itself.
        }
    }
}

public enum ExceptionChoice
{
    // No answer yet, or one that could not be read.
    None,
    Continue,
    SaveAndClose,
    Close
}

// What the player pressed in the window. BEM writes it beside the crash record and the Companion, which
// is holding the game's frame, reads it by hand: the "decision" values are a contract with
// CrashGuardWindow in the Companion.
public static class ExceptionDecision
{
    public const string Suffix = ".decision";

    // Not ".json": CrashGuardRecord.ReadNewest reads every .json in the folder as a record.
    public static string PathFor(string recordPath) =>
        Path.Combine(
            Path.GetDirectoryName(recordPath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(recordPath) + Suffix);

    public static string Text(ExceptionChoice choice) => choice switch
    {
        ExceptionChoice.Continue => "continue",
        ExceptionChoice.SaveAndClose => "saveAndClose",
        ExceptionChoice.Close => "close",
        _ => string.Empty
    };

    // Written whole or not at all, since the Companion polls for the file and must never read half of it.
    public static void Write(string recordPath, ExceptionChoice choice)
    {
        if (choice == ExceptionChoice.None)
            throw new ArgumentException("A decision must be one the Companion can act on.", nameof(choice));

        var path = PathFor(recordPath);
        var temp = path + ".tmp";

        File.WriteAllText(temp, "{\"decision\":\"" + Text(choice) + "\"}");
        File.Move(temp, path, overwrite: true);
    }

    public static ExceptionChoice Read(string recordPath)
    {
        try
        {
            var path = PathFor(recordPath);

            if (!File.Exists(path))
                return ExceptionChoice.None;

            using var document = JsonDocument.Parse(File.ReadAllText(path));

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("decision", out var decision)
                || decision.ValueKind != JsonValueKind.String)
                return ExceptionChoice.None;

            return decision.GetString() switch
            {
                "continue" => ExceptionChoice.Continue,
                "saveAndClose" => ExceptionChoice.SaveAndClose,
                "close" => ExceptionChoice.Close,
                _ => ExceptionChoice.None
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return ExceptionChoice.None;
        }
    }
}
