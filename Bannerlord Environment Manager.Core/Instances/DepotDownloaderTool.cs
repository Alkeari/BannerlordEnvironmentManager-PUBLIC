using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Instances;

// DepotDownloader is GPL-2.0. BEM never links it: it is downloaded at first use into
// InstanceLayout.ToolsFolder and run as its own windowless process, which keeps its license and
// BEM's cleanly apart.
//
// Two ways to sign in, because one of them is unusable on a remote desktop. -qr shows a code for the
// Steam mobile app to scan, which needs a second device when the screen being scanned is the phone
// doing the viewing. The account route answers DepotDownloader's own prompts on its standard input:
// the password is typed into BEM, handed to that process and kept nowhere else, and -remember-password
// leaves the token in DepotDownloader's tool folder so every later download signs in silently.
//
// The running Steam client's own session cannot be borrowed for this. It keeps no readable token: on
// 2026-09-04 config.vdf on this machine held an Accounts block with a SteamID and nothing else, and no
// file under Steam\config carried a JWT. Its console command download_depot, which would have needed
// no second sign-in at all, is refused by Valve ("Failed to get manifest request code, Access Denied")
// for a manifest it will not grant a request code for.
public enum SteamSignInKind
{
    QrCode,
    Account
}

// QrCode carries no name. Account carries the Steam account name; the password, when one is needed at
// all, arrives through the prompt handler and is never part of this record or of a command line.
public sealed record SteamSignIn(SteamSignInKind Kind, string AccountName)
{
    public static SteamSignIn QrCode { get; } = new(SteamSignInKind.QrCode, string.Empty);

    public static SteamSignIn ForAccount(string accountName) => new(SteamSignInKind.Account, accountName);
}

public enum SteamPromptKind
{
    Password,
    GuardCode
}

public sealed record SteamPrompt(SteamPromptKind Kind, string Question);

// Answers one of DepotDownloader's prompts, or null to give up on the sign-in.
public delegate Task<string?> SteamPromptHandler(SteamPrompt prompt);

// What the output reader needs from the running child: the three things it does to the process
// rather than to the output. A real run hands it the process; a test hands it its own, which is
// what lets the reader's early exits be taken without a Steam sign-in to reach them.
public interface IDepotDownloaderChannel
{
    // True when the answer reached the process, false when it did not because DepotDownloader had
    // already exited. Which of the two happened decides the message the user is shown, so it is
    // reported rather than thrown.
    Task<bool> TryAnswerAsync(string answer, CancellationToken cancellationToken);

    // Ends the child's own read of standard input without ending the child.
    void CloseInput();

    // Ends the child.
    void End();
}

// What one run of DepotDownloader did, for the caller that has to decide what happens next.
// UnansweredPrompt is the question BEM had no answer for, and BranchFallbacks are the depots Steam
// had no such branch for.
//
// GuardCodeAnswered says BEM produced a Steam Guard code for this run, whether or not the write
// reached the process, and AnswerUndelivered says the process had already exited when BEM tried to
// write.
//
// Only the exit code decides whether the run worked. DepotDownloader is the one that knows: it
// exits zero when it signed in and fetched what it was asked for, and neither a code BEM typed nor
// a write that landed nowhere changes that. Judging those instead told the user three times in a
// row that a sign-in which had in fact succeeded had failed, and asked for another Steam Guard
// code each time.
public sealed record DownloadRunResult(
    int ExitCode,
    string Message,
    IReadOnlyList<BranchFallback> BranchFallbacks,
    string? UnansweredPrompt,
    bool GuardCodeAnswered = false,
    bool AnswerUndelivered = false)
{
    public bool Succeeded => ExitCode == 0;
}

// log receives one English line per thing worth knowing about a run: the invocation, each stage,
// the exit code and the failure. It is a delegate rather than a logger type because Core has no UI
// and no file layout of its own; the app hands it LoggingService.Log.
public sealed partial class DepotDownloaderTool(string gamesRoot, Action<string>? log = null)
{
    public const int BannerlordAppId = GameDlc.BaseAppId;

    // The exact release BEM was written against, not whatever SteamRE published last night. The
    // three sentences PromptIn matches are read out of this build's own strings, and answering a
    // question on standard input means knowing its wording: pointing at releases/latest let the tool
    // upgrade itself under a matcher that could not follow it, and a reworded prompt then left the
    // run waiting on an answer nobody was going to type. Moving this forward is a deliberate change
    // to BEM, made after the new build's prompts have been read.
    public const string PinnedRelease = "DepotDownloader_3.4.0";

    private const string ReleasesUrl =
        $"https://api.github.com/repos/SteamRE/DepotDownloader/releases/tags/{PinnedRelease}";
    private const string AssetName = "DepotDownloader-windows-x64.zip";
    private const string ExeName = "DepotDownloader.exe";
    private const string VersionFileName = "version.txt";

    private const string SignInBanner = "Use the Steam Mobile App to sign in with this QR code:";
    private const string QrChangedBanner = "The QR code has changed:";
    private const string TotalDownloadedPrefix = "Total downloaded:";

    [GeneratedRegex(@"^\s*(?<pct>\d+(?:\.\d+)?)\s*%")]
    private static partial Regex PercentPattern { get; }

    // Pure: the caller owns state and feeds each stdout line back in with the progress it produced
    // last time, which is what lets this be tested against the tool's real output shapes with no
    // process involved.
    public static DownloadProgress? ReadLine(string line, DownloadProgress current)
    {
        if (line is null)
            return null;

        if (line == SignInBanner || line == QrChangedBanner)
            return current with { Stage = DownloadStage.AwaitingSignIn, Message = line, QrBlock = string.Empty };

        var percentMatch = PercentPattern.Match(line);

        // Both branches leave AwaitingSignIn, so the QR block is cleared here rather than carried
        // forward by the with-expression's default of keeping every field it does not name: once
        // sign-in is done, nothing after it should still be able to show that code.
        if (percentMatch.Success
            && double.TryParse(percentMatch.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            return current with { Stage = DownloadStage.Downloading, Message = line, Percent = (int)percent, QrBlock = string.Empty };

        if (line.StartsWith(TotalDownloadedPrefix, StringComparison.Ordinal))
            return current with { Stage = DownloadStage.Done, Message = line, QrBlock = string.Empty };

        if (current.Stage == DownloadStage.AwaitingSignIn)
        {
            var block = current.QrBlock.Length == 0 ? line : current.QrBlock + Environment.NewLine + line;
            return current with { QrBlock = block };
        }

        return null;
    }

    // Resolved once per tool instance, and one tool instance is one download flow. Every run
    // called EnsureInstalledAsync again, which asks GitHub for the latest release before the process
    // starts: that is a network round trip sitting between the moment a Steam Guard code is typed
    // and the moment DepotDownloader needs it, and the whole point of collecting the code up front
    // is that nothing sits in that gap.
    private string? installedExePath;

    public async Task<string> EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        if (installedExePath is { } resolved && File.Exists(resolved))
            return resolved;

        installedExePath = await ResolveInstalledAsync(cancellationToken);

        return installedExePath;
    }

    private async Task<string> ResolveInstalledAsync(CancellationToken cancellationToken)
    {
        var toolsFolder = InstanceLayout.ToolsFolder(gamesRoot);
        Directory.CreateDirectory(toolsFolder);

        var exePath = Path.Combine(toolsFolder, ExeName);
        var versionPath = Path.Combine(toolsFolder, VersionFileName);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BannerlordEnvironmentManager");

        string releaseJson;

        try
        {
            releaseJson = await client.GetStringAsync(ReleasesUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && File.Exists(exePath))
        {
            _ = ex;
            return exePath;
        }

        using var document = JsonDocument.Parse(releaseJson);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;

        if (File.Exists(exePath) && File.Exists(versionPath) && File.ReadAllText(versionPath).Trim() == tag)
            return exePath;

        string? downloadUrl = null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var name) && name.GetString() == AssetName
                    && asset.TryGetProperty("browser_download_url", out var url))
                {
                    downloadUrl = url.GetString();
                    break;
                }
            }
        }

        if (downloadUrl is null)
            throw new InvalidOperationException("The latest DepotDownloader release does not carry a windows-x64 asset.");

        var zipPath = Path.Combine(toolsFolder, "DepotDownloader.zip");

        await using (var stream = await client.GetStreamAsync(downloadUrl, cancellationToken))
        await using (var file = File.Create(zipPath))
            await stream.CopyToAsync(file, cancellationToken);

        ZipFile.ExtractToDirectory(zipPath, toolsFolder, overwriteFiles: true);
        File.Delete(zipPath);
        await File.WriteAllTextAsync(versionPath, tag, cancellationToken);

        return exePath;
    }

    public static string ArgumentsFor(string branch, string targetFolder, SteamSignIn signIn) =>
        ArgumentsFor(branch, targetFolder, signIn, resuming: false);

    // resuming adds -validate, which makes DepotDownloader check every file against the manifest
    // rather than work out what to fetch from the manifest it recorded as installed. A folder that
    // already holds part of a download is exactly the case where those two disagree: the record says
    // the depot is done, the disk is missing files, and without -validate the run finishes having
    // transferred nothing.
    public static string ArgumentsFor(string branch, string targetFolder, SteamSignIn signIn, bool resuming) =>
        ArgumentsFor(BannerlordAppId, branch, targetFolder, signIn, resuming);

    // A DLC is its own Steam app, downloaded into the game folder the base install already occupies:
    // the shape of the invocation is identical to the base download, with the DLC's own app id in
    // place of BannerlordAppId and no assumption that anything about the base download changes.
    public static string ArgumentsForDlc(int dlcAppId, string branch, string targetFolder, SteamSignIn signIn, bool resuming = false) =>
        ArgumentsFor(dlcAppId, branch, targetFolder, signIn, resuming);

    private static string ArgumentsFor(int appId, string branch, string targetFolder, SteamSignIn signIn, bool resuming)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        ArgumentNullException.ThrowIfNull(signIn);

        var common = $"-app {appId} -branch {branch} -dir \"{targetFolder}\" -remember-password";

        if (resuming)
            common += " -validate";

        return WithSignIn(common, signIn);
    }

    // Signing in is its own short run, before the long one. DepotDownloader has no login-only mode -
    // its own usage text lists -app, -pubfile and -ugc and nothing else - so the cheapest invocation
    // that actually authenticates is one depot's manifest with -manifest-only, which writes a
    // readable manifest and transfers no game content at all. What it does do is store the token
    // -remember-password asks for, so the download that follows needs no standard input: a Steam
    // Guard prompt answered up front cannot interrupt 58 GB an hour later.
    //
    // depotId is optional because the branch's depot list comes from a network fetch that can fail;
    // without one, every depot's manifest is fetched, which is still manifests rather than content.
    public static string SignInArgumentsFor(int appId, string branch, string? depotId, string targetFolder, SteamSignIn signIn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        ArgumentNullException.ThrowIfNull(signIn);

        var common = $"-app {appId} -branch {branch} -dir \"{targetFolder}\" -remember-password -manifest-only";

        if (!string.IsNullOrWhiteSpace(depotId))
            common += $" -depot {depotId}";

        return WithSignIn(common, signIn);
    }

    private static string WithSignIn(string common, SteamSignIn signIn) =>
        signIn.Kind == SteamSignInKind.Account
            ? $"{common} -username \"{signIn.AccountName}\""
            : $"{common} -qr";

    // One log line per stage change, and one per ten percent while downloading. A 58 GB download
    // reports progress several times a second, and a log carrying every report would bury the run
    // that matters in it.
    public static string? LogLineFor(DownloadProgress? previous, DownloadProgress current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (previous is null || previous.Stage != current.Stage)
            return $"DepotDownloader stage: {current.Stage} ({current.Percent}%)";

        return current.Stage == DownloadStage.Downloading && current.Percent / 10 > previous.Percent / 10
            ? $"DepotDownloader stage: {current.Stage} ({current.Percent}%)"
            : null;
    }

    // Whether this folder already holds something a download would carry on from.
    public static bool IsResume(string targetFolder)
    {
        try
        {
            return Directory.Exists(targetFolder) && Directory.EnumerateFileSystemEntries(targetFolder).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The three sentences DepotDownloader 3.4.0 writes when it is waiting on standard input, taken
    // from the strings in the shipped executable. PinnedRelease is what keeps this list and the
    // running tool the same build; HasStalled is what makes a question it does not name loud rather
    // than endless if they ever come apart. The password prompt is DepotDownloader's own and
    // goes to standard output; the two Steam Guard prompts belong to SteamKit2's
    // UserConsoleAuthenticator and go to standard error, which is why both streams are scanned for
    // questions rather than only the one.
    //
    // A run is never prompted for an account name: BEM always passes -username, or -qr, which asks
    // for nothing on standard input at all.
    private const string PasswordPromptPrefix = "Enter account password for \"";
    private const string PasswordPromptSuffix = "\": ";
    private const string DevicePrompt = "STEAM GUARD! Please enter your 2-factor auth code from your authenticator app: ";
    private const string EmailPromptPrefix = "STEAM GUARD! Please enter the auth code sent to the email at ";

    // A question, and nothing that merely looks like one. Matching a shape instead - a fragment
    // ending in a colon and a space, carrying the word "code" or "password" - cannot tell a question
    // from a progress line that has not finished printing, and DepotDownloader prints
    // "Got manifest request code for depot 228988 from app 228980, manifest ..., result: OK", which
    // is momentarily exactly that shape. BEM read it as a Steam Guard prompt: it stopped a download
    // dead for want of an answer, and asked for a code no process was waiting on. Anything not named
    // here is output.
    public static SteamPrompt? PromptIn(string pendingOutput)
    {
        if (string.IsNullOrEmpty(pendingOutput))
            return null;

        var question = pendingOutput.TrimStart();

        if (question.StartsWith(PasswordPromptPrefix, StringComparison.Ordinal)
            && question.EndsWith(PasswordPromptSuffix, StringComparison.Ordinal))
            return new SteamPrompt(SteamPromptKind.Password, question.TrimEnd());

        if (question == DevicePrompt)
            return new SteamPrompt(SteamPromptKind.GuardCode, question.TrimEnd());

        return question.StartsWith(EmailPromptPrefix, StringComparison.Ordinal)
            && question.EndsWith(": ", StringComparison.Ordinal)
            ? new SteamPrompt(SteamPromptKind.GuardCode, question.TrimEnd())
            : null;
    }

    // How long a run may say nothing at all before BEM treats it as stopped rather than working.
    // DepotDownloader writes something several times a second while it fetches, and the one thing
    // that makes it silent indefinitely is a read of standard input for a question BEM did not
    // recognize: nothing then answers it, both readers sit on ReadAsync, and the Versions page shows
    // the last progress line with no error and no end. Ten minutes is far longer than any quiet
    // stretch a working run has, and short enough that a stuck one is not left overnight.
    public static readonly TimeSpan SilenceLimit = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan SilenceCheckInterval = TimeSpan.FromSeconds(15);

    // Whether a run that has said nothing for this long has stopped rather than paused. Pure, because
    // what has to be right about it is which silences do not count: a question BEM has put on screen
    // is waiting on the user, and the QR sign-in shows a code with nothing for the tool to say until
    // it is scanned. Both are silences the user can see the reason for, and neither is a stall.
    public static bool HasStalled(TimeSpan silence, DownloadStage stage, bool questionOutstanding) =>
        silence >= SilenceLimit && stage != DownloadStage.AwaitingSignIn && !questionOutstanding;

    public Task<DownloadRunResult> DownloadAsync(
        string branch,
        string targetFolder,
        SteamSignIn signIn,
        SteamPromptHandler prompt,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken) =>
        RunAsync(
            ArgumentsFor(BannerlordAppId, branch, targetFolder, signIn, IsResume(targetFolder)),
            targetFolder, prompt, progress, cancellationToken);

    // A DLC app fetched into a game folder the base download already filled. Everything about the
    // run is identical to the base download except which app id Steam is asked for; the base
    // download's own behavior is untouched by this existing alongside it.
    public Task<DownloadRunResult> DownloadDlcAsync(
        int dlcAppId,
        string branch,
        string targetFolder,
        SteamSignIn signIn,
        SteamPromptHandler prompt,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken) =>
        RunAsync(
            ArgumentsFor(dlcAppId, branch, targetFolder, signIn, IsResume(targetFolder)),
            targetFolder, prompt, progress, cancellationToken);

    // The sign-in that happens before the download, on the cheapest run that authenticates. It runs
    // in a folder of its own under the tool folder, which is deleted afterward: what it leaves
    // behind is one readable manifest nobody reads, and the token, which DepotDownloader keeps
    // beside its own executable.
    public async Task<DownloadRunResult> SignInAsync(
        int appId,
        string branch,
        string? depotId,
        SteamSignIn signIn,
        SteamPromptHandler prompt,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signIn);

        var folder = SignInFolder(gamesRoot);

        try
        {
            return await RunAsync(
                SignInArgumentsFor(appId, branch, depotId, folder, signIn),
                folder, prompt, progress, cancellationToken);
        }
        finally
        {
            DeleteSignInFolder(folder);
        }
    }

    public static string SignInFolder(string gamesRoot) =>
        Path.Combine(InstanceLayout.ToolsFolder(gamesRoot), "SignIn");

    private void DeleteSignInFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"The Steam sign-in folder '{folder}' could not be cleared: {ex.Message}");
        }
    }

    private void Log(string message) => log?.Invoke(message);

    private async Task<DownloadRunResult> RunAsync(
        string arguments,
        string targetFolder,
        SteamPromptHandler prompt,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var exePath = await EnsureInstalledAsync(cancellationToken);

        Directory.CreateDirectory(targetFolder);

        var current = new DownloadProgress(DownloadStage.Starting, Strings.Current["Core.Instances.DepotDownloader.Starting"], string.Empty, 0);
        progress.Report(current);

        var output = new StringBuilder();
        var lines = new List<string>();

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Kept at the tool's own folder so nothing the tool writes relative to where it started
            // lands in BEM's install folder. It is not what decides where the remembered Steam token
            // goes: DepotDownloader keeps account.config in isolated storage scoped to its own
            // assembly, which is derived from the executable's path and not from this.
            WorkingDirectory = Path.GetDirectoryName(exePath)!
        };

        // The password is answered on standard input and never appears here, so the invocation can be
        // logged as it stands: the account name is in it, and nothing else about the account is.
        Log($"DepotDownloader: {Path.GetFileName(exePath)} {arguments}");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start DepotDownloader.");

        // Two exit paths this run does not own, wired the moment the child exists.
        //
        // The registration is what makes a stop reach the process rather than only the awaits below
        // it: killing DepotDownloader closes the pipes the readers are sitting on, which is what
        // ends them. Waiting for a reader to notice the token on its own is not deterministic, and a
        // token that only stopped BEM waiting would leave the download running.
        //
        // Supervise covers the path no code of BEM's runs on at all: BEM itself ending.
        ChildProcessSupervision.Supervise(process);

        using var stopTheProcess = cancellationToken.Register(() => Kill(process));

        var lastError = string.Empty;
        var logged = (DownloadProgress?)null;
        var prompts = new PromptOutcome();
        var activity = new RunActivity();

        var channel = new ProcessChannel(process);

        var stdoutTask = ReadStreamAsync(process.StandardOutput, channel, line =>
        {
            output.AppendLine(line);
            lines.Add(line);
            current = ReadLine(line, current) ?? current;

            if (LogLineFor(logged, current) is { } stageLine)
            {
                Log(stageLine);
                logged = current;
            }

            progress.Report(current);
        }, prompt, prompts, activity, cancellationToken);

        // Standard error is read the same way rather than line by line, because the Steam Guard
        // prompts are written there and a prompt carries no newline: a line reader would sit on one
        // until the process gave up on an answer BEM was never going to be asked for.
        var stderrTask = ReadStreamAsync(process.StandardError, channel, line =>
        {
            output.AppendLine(line);
            lines.Add(line);

            if (line.Trim().Length > 0)
                lastError = line;
        }, prompt, prompts, activity, cancellationToken);

        using var runIsOver = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var watchdog = WatchForSilenceAsync(process, activity, () => current.Stage, runIsOver.Token);

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(cancellationToken);

            // The kill above closes the pipes, so a stop can leave the readers finishing normally
            // and this method walking on to report an exit code of -1 as a failed download. The
            // stop is the answer whenever one was asked for.
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            Log("BEM stopped DepotDownloader: the run was canceled.");
            Kill(process);
            throw;
        }
        catch (Exception ex)
        {
            // Every exception that is not a cancellation used to leave the child running: `using var
            // process` disposes the handle, which does not terminate what it points at, and a
            // prompt handler that throws (a second ContentDialog, which WinUI refuses) throws out of
            // the reader rather than out of the process. The run is over either way, so the process
            // goes with it.
            Log($"BEM stopped DepotDownloader: the run ended in {ex.GetType().Name} ({ex.Message}).");
            Kill(process);
            throw;
        }
        finally
        {
            // The watchdog outlives every path out of the block above, including the two that
            // rethrow, so it is stopped and awaited here rather than left as a task nothing observes.
            await runIsOver.CancelAsync();
            await watchdog;
        }

        WriteLog(exePath, arguments, output.ToString(), process.ExitCode);

        var fallbacks = BranchFallbackReader.ReadAll(lines);

        foreach (var fallback in fallbacks)
            Log($"Depot {fallback.DepotId} publishes no '{fallback.RequestedBranch}' branch, so DepotDownloader used '{fallback.UsedBranch}'.");

        if (prompts.Unanswered is not null)
            Log(UnansweredLogLine(prompts.Unanswered, prompts.EndedForQuestion));

        if (prompts.AnswerUndelivered)
            Log($"DepotDownloader had already exited when BEM answered '{prompts.UndeliveredQuestion}', so the answer reached nothing.");

        Log($"DepotDownloader exited with code {process.ExitCode}.");

        // That a code was asked for, and whether Steam went on with the run after it, is the one
        // thing the logs never said and the one thing that would settle why a second prompt appears.
        // The code itself is never written here or anywhere else.
        if (prompts.GuardCodeAnswered)
        {
            Log(process.ExitCode == 0
                ? "Steam asked for a Steam Guard code in this run and accepted the answer BEM gave it."
                : "Steam asked for a Steam Guard code in this run and the run still ended in failure.");
        }

        if (process.ExitCode != 0)
        {
            // A closed pipe explains the run better than the exit code or the tool's last words do,
            // so it wins over both: "The pipe is being closed" was the whole explanation the user
            // got for a failed 58 GB download.
            current = current with
            {
                Stage = DownloadStage.Failed,
                Message = prompts.AnswerUndelivered
                    ? Strings.Current["Core.Instances.DepotDownloader.AnswerUndelivered"]
                    : lastError.Length > 0 ? lastError : Strings.Current["Core.Instances.DepotDownloader.ExitedWithError"],
                QrBlock = string.Empty
            };
            progress.Report(current);

            Log($"DepotDownloader failed: {current.Message} Its last words were: {LastWordsOf(lines)}");
        }

        return new DownloadRunResult(
            process.ExitCode, current.Message, fallbacks, prompts.Unanswered,
            prompts.GuardCodeAnswered, prompts.AnswerUndelivered);
    }

    // What the log says about a run BEM could not answer a question in. Pure, because the thing that
    // has to be right about it is which question it names: the run is ended by the question that
    // arrives after standard input is closed, and reporting the earlier one instead described a
    // question the user had already been asked and declined rather than the one nobody ever saw.
    public static string UnansweredLogLine(string unanswered, string? endedFor) =>
        string.IsNullOrEmpty(endedFor)
            ? $"BEM had no answer for '{unanswered}', so it closed DepotDownloader's standard input and left the run to end on its own."
            : string.Equals(endedFor, unanswered, StringComparison.Ordinal)
                ? $"BEM had no answer for '{unanswered}', so it closed DepotDownloader's standard input, and ended the run when the same question came back."
                : $"BEM had no answer for '{unanswered}', so it closed DepotDownloader's standard input, and ended the run when it was asked '{endedFor}' with nothing left to answer it.";

    private static string LastWordsOf(IReadOnlyList<string> lines) =>
        lines.LastOrDefault(line => line.Trim().Length > 0)?.Trim() ?? string.Empty;

    public static string LogPathFor(string exePath) =>
        Path.Combine(Path.GetDirectoryName(exePath)!, "depotdownloader.log");

    // Everything the tool said, kept for the run that just happened. A download that transfers
    // nothing and exits cleanly says why on its own output, and BEM was dropping every word of it.
    // The password never reaches here: it goes to standard input, and the tool never echoes it.
    private static void WriteLog(string exePath, string arguments, string output, int exitCode)
    {
        try
        {
            File.WriteAllText(
                LogPathFor(exePath),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}"
                + $"{Path.GetFileName(exePath)} {arguments}{Environment.NewLine}"
                + $"exit code {exitCode}{Environment.NewLine}{Environment.NewLine}{output}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
        }
    }

    // Ends a run that has stopped saying anything. The pipes give no signal for it: a tool waiting on
    // a read of standard input holds them open and writes nothing, so both readers stay inside
    // ReadAsync and Task.WhenAll never returns. The only evidence is the silence itself.
    private async Task WatchForSilenceAsync(
        Process process, RunActivity activity, Func<DownloadStage> stage, CancellationToken runIsOver)
    {
        while (true)
        {
            try
            {
                await Task.Delay(SilenceCheckInterval, runIsOver);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (process.HasExited)
                return;

            if (!HasStalled(activity.Silence, stage(), activity.QuestionOutstanding))
                continue;

            Log($"DepotDownloader has written nothing for {SilenceLimit.TotalMinutes:F0} minutes and has "
                + "asked no question BEM recognizes, so BEM ended the run. A prompt BEM cannot answer is "
                + $"what this looks like: the prompts it answers are {PinnedRelease}'s own wording.");

            Kill(process);

            return;
        }
    }

    // When the child last said anything, and whether a question of its own is on screen waiting for
    // the user. Both readers write to it and the watchdog reads it, so every field goes through an
    // interlocked or volatile access rather than relying on the awaits happening to serialize them.
    public sealed class RunActivity
    {
        private long lastTicks = Environment.TickCount64;

        private int questionOutstanding;

        public TimeSpan Silence => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref lastTicks));

        public bool QuestionOutstanding => Volatile.Read(ref questionOutstanding) != 0;

        public void Seen() => Interlocked.Exchange(ref lastTicks, Environment.TickCount64);

        public void AskingTheUser() => Interlocked.Exchange(ref questionOutstanding, 1);

        public void AnsweredOrDeclined()
        {
            Interlocked.Exchange(ref questionOutstanding, 0);
            Seen();
        }
    }

    // What the prompts in one run did, gathered where the readers can write it and RunAsync can read
    // it once they have finished. Both streams share one of these, which is safe because
    // DepotDownloader writes a prompt and then waits: two questions are never outstanding at once.
    public sealed class PromptOutcome
    {
        public string? Unanswered { get; set; }

        public bool InputClosed { get; set; }

        // The question the run was ended for, which is not always the question BEM first had no
        // answer for: once standard input is closed anything DepotDownloader asks next goes
        // unanswered, whether or not it repeats. Empty until a second question arrives.
        public string EndedForQuestion { get; set; } = string.Empty;

        public bool GuardCodeAnswered { get; set; }

        public bool AnswerUndelivered { get; set; }

        public string UndeliveredQuestion { get; set; } = string.Empty;
    }

    // Reads one of the child's two output streams, answering the questions it finds in it.
    //
    // The stream is read in chunks and split by DepotDownloaderOutputReader, so a line delivered in
    // two pieces is still one line and a half-written line is never a question.
    public static async Task ReadStreamAsync(
        TextReader stream, IDepotDownloaderChannel channel, Action<string> onLine, SteamPromptHandler prompt,
        PromptOutcome outcome, RunActivity activity, CancellationToken cancellationToken)
    {
        var reader = new DepotDownloaderOutputReader();
        var buffer = new char[4096];
        int count;

        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            activity.Seen();

            foreach (var item in reader.Feed(new string(buffer, 0, count)))
            {
                if (item.Prompt is not { } question)
                {
                    onLine(item.Text);
                    continue;
                }

                // A prompt carries no newline, so it never reaches onLine on its own: writing it
                // down here is what keeps a transcript from stopping mid-sentence.
                onLine(question.Question);

                if (outcome.InputClosed)
                {
                    // Standard input is already closed, so no answer BEM could collect for this
                    // question could be delivered: putting a dialog up for it would ask the user to
                    // type something that goes nowhere. DepotDownloader cannot get past the read on
                    // its own either, and no depot data moves while it is waiting to sign in, so
                    // ending it here costs no transfer. This is the question that actually went
                    // unanswered, whether or not it repeats the first one, and the log says so.
                    outcome.EndedForQuestion = question.Question;
                    channel.End();
                    FlushRest();
                    return;
                }

                string? answer;

                activity.AskingTheUser();

                try
                {
                    answer = await prompt(question);
                }
                finally
                {
                    activity.AnsweredOrDeclined();
                }

                if (answer is null)
                {
                    // Closing standard input rather than the process. DepotDownloader's own read
                    // then ends and it stops on its own terms, with its own exit code and its own
                    // last words, and everything it had already transferred stays where it is.
                    // Killing here is what turned one misread progress line into a dead download.
                    outcome.Unanswered = question.Question;
                    outcome.InputClosed = true;
                    channel.CloseInput();
                    continue;
                }

                if (question.Kind == SteamPromptKind.GuardCode)
                    outcome.GuardCodeAnswered = true;

                if (!await channel.TryAnswerAsync(answer, cancellationToken))
                {
                    outcome.AnswerUndelivered = true;
                    outcome.UndeliveredQuestion = question.Question;
                    FlushRest();
                    return;
                }
            }
        }

        FlushRest();

        // The tool's last unterminated line is the sentence the incomplete-download message tells the
        // user to go and read, so every way out of this method has to write it down. The two early
        // returns above did not, and dropped it from depotdownloader.log and from LastLineOf on the
        // two runs that end in exactly the trouble it explains.
        void FlushRest()
        {
            if (reader.Flush() is { } rest)
                onLine(rest);
        }
    }

    // The running child, for the reader that answers its questions. Both of this run's readers share
    // one of these, the way they already share the process itself.
    private sealed class ProcessChannel(Process process) : IDepotDownloaderChannel
    {
        public async Task<bool> TryAnswerAsync(string answer, CancellationToken cancellationToken)
        {
            try
            {
                await process.StandardInput.WriteLineAsync(answer.AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);

                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // DepotDownloader exited while the question was outstanding, so its standard input is
                // gone and writing to it throws "The pipe is being closed". That sentence was reaching
                // the user as the entire account of what went wrong; it is reported here as what it is
                // rather than thrown at a caller that can only repeat it. It does not decide whether
                // the run worked: the exit code does.
                _ = ex;

                return false;
            }
        }

        public void CloseInput() => DepotDownloaderTool.CloseInput(process);

        public void End() => Kill(process);
    }

    // Nothing here may throw: the process can exit between the question and the close, and standard
    // input is shared with the other stream's reader.
    private static void CloseInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _ = ex;
        }
    }

    // Nothing here may throw. It runs from a cancellation callback, which raises anything that
    // escapes it out of CancellationTokenSource.Cancel and so out of the button that was pressed.
    // Kill(entireProcessTree) reports a child it could not end as an AggregateException, and a
    // process that exited between the check and the call as an InvalidOperationException: neither is
    // worth turning a stop into a crash dialog over.
    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or Win32Exception or AggregateException or NotSupportedException)
        {
            _ = ex;
        }
    }
}
