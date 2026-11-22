using System.Collections.ObjectModel;
using BannerlordEnvironmentManager.Core.Butr;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.DryRun;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules.KnownIssues;
using BannerlordEnvironmentManager.Core.Nexus;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels;

// The Better Exception Window view: the exception, its stack with every Harmony patch frame named by the
// mod that owns it, and the modules the crash went through. It reads the Companion's crash-guard record when
// there is one, and otherwise the engine's own error log, which carries no stack, so then only the mods
// that patch one method together are listed.
public sealed partial class CrashAnalysisViewModel : BaseViewModel
{
    public ObservableCollection<string> Modules { get; } = [];

    public ObservableCollection<string> Frames { get; } = [];

    [ObservableProperty]
    public partial string Headline { get; set; } = Strings.Current["CrashView.Headline.None"];

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    // The share link. Uploading is the owner's call every time: the page asks first, showing the exact
    // report, and only then does UploadShareAsync send it. Nothing here runs on its own.
    private readonly ButrContribution share = new(
        new ButrCrashHttpTransport(),
        new ButrSubmissionLedger(Path.Combine(NexusApiKeyStore.DefaultDirectory, ButrSubmissionLedger.FileName)));

    private ButrPayload? preparedShare;

    private CancellationTokenSource? shareCancellation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareIdle))]
    public partial bool ShareBusy { get; set; }

    public bool ShareIdle => !ShareBusy;

    [ObservableProperty]
    public partial string ShareStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ShareLink { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeleteLink { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeleteNote { get; set; } = string.Empty;

    // What the confirmation says about the report it is about to publish, and the exact text sent.
    public string ShareSummary { get; private set; } = string.Empty;

    public string SharePayload => preparedShare?.Json ?? string.Empty;

    // Builds the report from the newest crash on disk without sending anything. False means there is
    // nothing to share, and ShareStatus says why.
    public async Task<bool> PrepareShareAsync()
    {
        preparedShare = null;
        ShareLink = string.Empty;
        DeleteLink = string.Empty;
        DeleteNote = string.Empty;
        ShareBusy = true;

        try
        {
            var built = await Task.Run(SettingsViewModel.BuildNewestCrashSubmission);

            if (built.Submission is null)
            {
                ShareStatus = built.Reason;
                return false;
            }

            var payload = share.PrepareShare(built.Submission);

            if (!payload.IsReady)
            {
                ShareStatus = payload.Reason;
                return false;
            }

            preparedShare = payload;
            ShareSummary = Strings.Current.Format("CrashView.Share.Ready", built.Reason, payload.Reason);
            ShareStatus = string.Empty;

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LoggingService.LogException(ex, "Failed to prepare a crash report to share");
            ShareStatus = Strings.Current["CrashView.Share.Failed"];

            return false;
        }
        finally
        {
            ShareBusy = false;
        }
    }

    // Sends the report PrepareShareAsync built, after the owner agreed to it.
    public async Task UploadShareAsync()
    {
        if (preparedShare is not { } payload)
            return;

        shareCancellation?.Cancel();
        shareCancellation?.Dispose();
        shareCancellation = new CancellationTokenSource();

        ShareBusy = true;
        ShareStatus = Strings.Current["CrashView.Share.Busy"];

        try
        {
            var result = await share.ShareAsync(payload, DateTimeOffset.UtcNow, shareCancellation.Token);

            ShareStatus = result.Message;
            ShareLink = result.ReportUrl ?? string.Empty;
            DeleteLink = result.DeleteUrl ?? string.Empty;
            DeleteNote = result.DeleteUrl is { } delete
                ? Strings.Current.Format("CrashView.Share.DeleteNote", delete)
                : string.Empty;

            if (result.WasSent)
                preparedShare = null;
        }
        catch (OperationCanceledException)
        {
            ShareStatus = Strings.Current["Core.Butr.Contribution.Canceled"];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LoggingService.LogException(ex, "Failed to share a crash report");
            ShareStatus = Strings.Current["CrashView.Share.Failed"];
        }
        finally
        {
            ShareBusy = false;
        }
    }

    // The page's async void handler ends here, so nothing it throws can take the app down.
    public void ShowShareFailure(Exception ex)
    {
        LoggingService.LogException(ex, "Failed to share a crash report");
        ShareBusy = false;
        ShareStatus = Strings.Current["CrashView.Share.Failed"];
    }

    [RelayCommand]
    private void CopyShareLink() => CopyToClipboard(ShareLink);

    [RelayCommand]
    private void CopyDeleteLink() => CopyToClipboard(DeleteLink);

    [RelayCommand]
    private void OpenShareLink()
    {
        // Only an address on BUTR's own domains is ever opened.
        if (ButrReportLink.Normalize(ShareLink) is not { } link)
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            LoggingService.LogException(ex, $"Failed to open {link}");
            ShareStatus = Strings.Current.Format("CrashView.Share.OpenFailed", link);
        }
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);

        try
        {
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            ShareStatus = Strings.Current.Format("CrashView.Share.Copied", text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            LoggingService.LogException(ex, "Failed to copy the crash report link to the clipboard");
            ShareStatus = Strings.Current["CrashView.Share.CopyFailed"];
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;

        try
        {
            var view = await Task.Run(Build);

            Headline = view.Headline;
            Message = view.Message;
            Notice = view.Notice;
            Replace(Modules, view.Modules);
            Replace(Frames, view.Frames);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LoggingService.LogException(ex, "Failed to read the crash analysis");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void Replace(ObservableCollection<string> target, IReadOnlyList<string> rows)
    {
        target.Clear();

        foreach (var row in rows)
            target.Add(row);
    }

    private sealed record View(string Headline, string Message, string Notice, IReadOnlyList<string> Modules, IReadOnlyList<string> Frames);

    private static View Build()
    {
        var strings = Strings.Current;
        var record = CrashGuardRecord.ReadNewest(CrashGuardRecord.DefaultFolder);
        var root = DiagnosticsViewModel.ResolveDataRoot() ?? InstanceDataRoot.ForMachine();
        var log = UnhandledExceptionLog.FindNewest(Path.Combine(root.ProgramData, "logs"));

        // The record is the richer evidence, so it wins unless the engine's log is from a later crash.
        var useLog = log is { } found && (record is null || found.WrittenUtc > record.Utc);

        if (record is null && log is null)
            return new View(strings["CrashView.Headline.None"], string.Empty, string.Empty, [], []);

        var install = DiagnosticsViewModel.ResolveInstallPath();
        var index = string.IsNullOrWhiteSpace(install) ? AssemblyIndex.Empty : AssemblyIndex.Build(install);
        var entries = DiagnosticsViewModel.ReadModuleEntries(install);
        var overlaps = index.Failed ? [] : HarmonyPatchOverlaps.Inspect(index, entries).Overlaps;

        if (useLog)
        {
            var block = log!.Value.Block;
            var modules = CrashAttributor.FromOverlaps(overlaps).Select(m => ModuleRow(strings, m)).ToList();

            return new View(
                strings.Format("CrashView.Headline.Log", block.CodeText, block.Address),
                string.Empty,
                strings["CrashView.Notice.NoStack"],
                modules.Count == 0 ? [strings["CrashView.Modules.None"]] : modules,
                []);
        }

        var typeOwner = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var assembly in index.Assemblies.Where(a => a.Origin == AssemblyOrigin.Module && a.ModuleId is not null))
        {
            foreach (var type in assembly.DefinedTypeFullNames)
                typeOwner.TryAdd(type, assembly.ModuleId!.Value.Value);
        }

        var result = GameDataFeeds.Extend(CrashAttributor.Attribute(
            record!.Stack,
            record.PatchOwners,
            overlaps,
            type => typeOwner.TryGetValue(type, out var owner) ? owner : null), entries,
            EngineLog.ReadRun(Path.Combine(root.ProgramData, "logs"), record.RunId));

        var frames = result.Frames.Select(f => FrameRow(strings, f)).ToList();
        var involved = result.Involved.Select(m => ModuleRow(strings, m)).ToList();
        var engineNotices = result.Involved.Where(m => m.Engine is not null).Select(m => ExceptionReport.EngineNotice(strings, m));

        return new View(
            strings.Format("CrashView.Headline.Guard", record.ExceptionType, record.Where, record.Utc.ToLocalTime().ToString("g")),
            record.Message,
            string.Join(Environment.NewLine + Environment.NewLine,
                engineNotices.Concat(record.PatchOwners is { Count: > 0 } ? [] : [strings["CrashView.Notice.NoOwners"]])),
            involved.Count == 0 ? [strings["CrashView.Modules.None"]] : involved,
            frames.Count == 0 ? [record.Stack] : frames);
    }

    private static string ModuleRow(ILocalizer strings, InvolvedModule module) =>
        ExceptionReport.ModuleRow(strings, module);

    private static string FrameRow(ILocalizer strings, AttributedFrame frame)
    {
        var name = $"{frame.Type}.{frame.Method}";

        if (frame.Modules.Count == 0)
            return name;

        var owners = string.Join(", ", frame.Modules);

        return strings.Format(frame.IsPatchFrame || frame.Source != FrameSource.ModAssembly ? "CrashView.Frame.Patched" : "CrashView.Frame.InMod", name, owners);
    }
}
