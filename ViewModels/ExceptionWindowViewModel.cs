using System.Collections.ObjectModel;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.DryRun;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels;

// The Bannerlord Exception Manager's window, for one crash record. It runs in a process of its own,
// started by the Companion inside the game while the game holds the failing frame, so it uses nothing of
// the shell: no page, no instance registry, no load order. The answer goes back as a file beside the
// record, and closing the window without one leaves the game to its own crash policy.
public sealed partial class ExceptionWindowViewModel(string recordPath) : BaseViewModel
{
    private ExceptionReport? _report;

    private string _log = string.Empty;

    public string RecordPath { get; } = recordPath;

    // The game's window as it stood when this start began, copied before the window opened over it.
    public GameFrame? GameFrame { get; init; }

    private string _screenshotPath = string.Empty;

    // The game that wrote the record, zero when the record does not name one.
    private int _gameProcessId;

    // Only while the game still holds the failing frame: after an answer there is no game to copy.
    [ObservableProperty]
    public partial bool CanSaveDump { get; set; }

    public ObservableCollection<string> Suspects { get; } = [];

    public ObservableCollection<string> Frames { get; } = [];

    public ObservableCollection<string> Mods { get; } = [];

    public ObservableCollection<string> Assemblies { get; } = [];

    public ObservableCollection<string> Patches { get; } = [];

    public ObservableCollection<string> Modules { get; } = [];

    // The file Open Folder selects: the newest thing saved beside the record, the record before any.
    private string _openTarget = recordPath;

    [ObservableProperty]
    public partial string Headline { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    // Empty for the first crash of a game session; from the second on, how many there have been.
    [ObservableProperty]
    public partial string CrashCount { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasCrashCount { get; set; }

    // The game build, once the game folder has been read.
    [ObservableProperty]
    public partial string GameVersion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasGameVersion { get; set; }

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Media.ImageSource? Screenshot { get; set; }

    [ObservableProperty]
    public partial bool HasScreenshot { get; set; }

    [ObservableProperty]
    public partial bool HasNoScreenshot { get; set; } = true;

    [ObservableProperty]
    public partial bool HasNotice { get; set; }

    [ObservableProperty]
    public partial string Guidance { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RawStack { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LogText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanContinue { get; set; }

    [ObservableProperty]
    public partial bool CanSave { get; set; }

    // The game is waiting on this window. False once answered, and for a crash the game could not hold.
    [ObservableProperty]
    public partial bool IsWaiting { get; set; }

    [ObservableProperty]
    public partial bool HasReport { get; set; }

    // True while a suspect is a mod that is still on in the load order the game reads next.
    [ObservableProperty]
    public partial bool CanTurnOffSuspects { get; set; }

    [ObservableProperty]
    public partial string SuspectsStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasSuspectsStatus { get; set; }

    // Raised once an answer is written: the window has nothing left to wait for.
    public event Action? Answered;

    public async Task LoadAsync()
    {
        var strings = Strings.Current;
        var record = await Task.Run(() => CrashGuardRecord.Read(RecordPath));

        if (record is null)
        {
            Headline = strings.Format("Exception.NoRecord", RecordPath);
            return;
        }

        // The record alone first, so the buttons are there at once: the game is frozen until one is pressed.
        Show(ExceptionReport.FromRecord(record));

        CrashCount = record.CrashNumber > 1 ? strings.Format("Exception.CrashNumber", record.CrashNumber) : string.Empty;
        HasCrashCount = CrashCount.Length > 0;
        // His three forms: continue and save, save only (the game is still held), neither (it is ending).
        var held = record.CanContinue || record.CanSave;
        IsWaiting = held;
        CanContinue = record.CanContinue;
        _gameProcessId = ExceptionReport.ProcessIdOf(record.RunId);
        CanSaveDump = held && _gameProcessId > 0;
        CanSave = record.CanSave;
        Guidance = strings[!held
            ? "Exception.Guidance.Ended"
            : !record.CanContinue
                ? "Exception.Guidance.SaveOnly"
                : (record.CanSave ? "Exception.Guidance.Held" : "Exception.Guidance.HeldNoSave")];

        await KeepTheScreenshotAsync();
        await LoadModulesAsync(record);

        try
        {
            var logsFolder = Path.Combine(InstanceDataRoot.ForMachine().ProgramData, "logs");
            var full = await Task.Run(() => ExceptionReport.Build(record, EngineLog.ReadRun(logsFolder, record.RunId)));
            var log = await Task.Run(() => ExceptionReport.ReadLogTail(logsFolder, record.RunId));

            Show(full);
            _log = log;
            CanTurnOffSuspects = await Task.Run(() => SuspectSwitch.Plan(
                record.GamePath ?? string.Empty, LoadOrderOfTheRunningGame(), full.SuspectIds).ChangesAnything);
            LogText = log.Length == 0 ? strings["Exception.Empty.Log"] : log;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or InvalidDataException or System.Xml.XmlException or BadImageFormatException)
        {
            // The record's own view stays on screen; only the mod scan is missing.
            LoggingService.LogException(ex, "Failed to read the mods behind a caught crash");
        }
    }

    // Every module the game process has loaded, native and managed. The game may already be gone, which
    // is told in plain words in the tab rather than raised.
    private async Task LoadModulesAsync(CrashGuardRecord record)
    {
        var strings = Strings.Current;

        if (_gameProcessId <= 0)
        {
            Replace(Modules, [], strings["Exception.Empty.Modules"]);
            return;
        }

        try
        {
            var processId = _gameProcessId;
            var rows = await Task.Run(() => LoadedModules.Read(processId, record.GamePath));

            Replace(Modules, [.. rows.Select(row => LoadedModules.Row(strings, row))], strings["Exception.Empty.Modules"]);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            Replace(Modules, [], strings["Exception.Empty.Modules"]);
        }
    }

    // Beside the record under the same name, as the report is. A picture that cannot be written is
    // only a missing tab: the answer buttons are what the game is waiting for.
    private async Task KeepTheScreenshotAsync()
    {
        if (GameFrame is null)
            return;

        try
        {
            var path = Path.ChangeExtension(RecordPath, ".png");

            await GameScreenshot.SaveAsync(GameFrame, path);

            _screenshotPath = path;
            Screenshot = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
            HasScreenshot = true;
            HasNoScreenshot = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException
                                       or System.Runtime.InteropServices.COMException)
        {
            LoggingService.LogException(ex, "Could not keep the picture of the game at the crash");
        }
    }

    private void Show(ExceptionReport report)
    {
        var strings = Strings.Current;

        _report = report;
        HasReport = true;
        Headline = report.Headline;
        Message = report.Record.Message;
        Notice = report.Notice;
        HasNotice = report.Notice.Length > 0;
        RawStack = report.Record.Stack;
        GameVersion = report.GameVersion.Length == 0 ? string.Empty : strings.Format("Core.DryRun.Exception.Report.GameVersion", report.GameVersion);
        HasGameVersion = GameVersion.Length > 0;

        Replace(Suspects, report.Suspects, strings["CrashView.Modules.None"]);
        Replace(Frames, report.Frames, string.Empty);
        Replace(Mods, report.Mods, strings["Exception.Empty.Mods"]);
        Replace(Assemblies, report.Assemblies, strings["Exception.Empty.Assemblies"]);
        Replace(Patches, report.Patches, strings["Exception.Empty.Patches"]);
    }

    private static void Replace(ObservableCollection<string> target, IReadOnlyList<string> rows, string whenEmpty)
    {
        target.Clear();

        foreach (var row in rows)
            target.Add(row);

        if (target.Count == 0 && whenEmpty.Length > 0)
            target.Add(whenEmpty);
    }

    [RelayCommand]
    private void Continue() => Answer(ExceptionChoice.Continue);

    [RelayCommand]
    private void SaveAndClose() => Answer(ExceptionChoice.SaveAndClose);

    [RelayCommand]
    private void CloseGame() => Answer(ExceptionChoice.Close);

    private void Answer(ExceptionChoice choice)
    {
        if (!IsWaiting)
            return;

        try
        {
            ExceptionDecision.Write(RecordPath, choice);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still waiting: the game has not been told anything, so the buttons stay.
            Status = Strings.Current.Format("Exception.Status.Failed", ex.Message);
            return;
        }

        IsWaiting = false;
        CanContinue = false;
        CanSaveDump = false;
        CanSave = false;
        Guidance = Strings.Current["Exception.Guidance.Answered"];
        Answered?.Invoke();
    }

    // The game this window answers is running, so the machine's paths are that game's own: BEM points
    // them at the version it starts for as long as the run lasts.
    private static LauncherDataStore LoadOrderOfTheRunningGame() => new(LauncherDataStore.GetDefaultPath());

    // For the next start only. The game behind this window keeps every mod it loaded, so the answer
    // buttons stay as they were.
    [RelayCommand]
    private async Task TurnOffSuspects()
    {
        if (_report is null || !CanTurnOffSuspects)
            return;

        var strings = Strings.Current;
        var report = _report;

        CanTurnOffSuspects = false;

        try
        {
            var result = await Task.Run(() => SuspectSwitch.TurnOff(
                report.Record.GamePath ?? string.Empty, LoadOrderOfTheRunningGame(), report.SuspectIds,
                strings["Exception.Suspects.BackupReason"]));

            SuspectsStatus = result.TurnedOff.Count == 0
                ? strings["Exception.Suspects.NothingToTurnOff"]
                : strings.Format("Exception.Suspects.TurnedOff", string.Join(", ", result.TurnedOff));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or InvalidOperationException or System.Xml.XmlException)
        {
            LoggingService.LogException(ex, "Failed to turn the suspect mods off");
            SuspectsStatus = strings.Format("Exception.Status.Failed", ex.Message);
            CanTurnOffSuspects = true;
        }

        HasSuspectsStatus = true;
    }

    [RelayCommand]
    private void CopyReport()
    {
        if (_report is null)
            return;

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(_report.ToText(_log));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            Status = Strings.Current["Exception.Status.Copied"];
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Status = Strings.Current.Format("Exception.Status.Failed", ex.Message);
        }
    }

    // Beside the record, under the same name, so the report of a crash is found with the crash. No
    // picker: the game is frozen behind this window and a file dialog is one more thing to answer.
    [RelayCommand]
    private void SaveReport()
    {
        if (_report is null)
            return;

        try
        {
            var path = Path.ChangeExtension(RecordPath, ".html");

            File.WriteAllText(path, _report.ToHtml(_log));
            Status = Strings.Current.Format("Exception.Status.Saved", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = Strings.Current.Format("Exception.Status.Failed", ex.Message);
        }
    }

    // The report and the newest save in one zip beside the record, for sending to a mod author. The
    // saves folder is the running game's own, for the same reason the load order is.
    [RelayCommand]
    private async Task SaveBundle()
    {
        if (_report is null)
            return;

        var strings = Strings.Current;
        var report = _report;
        var log = _log;
        var path = Path.ChangeExtension(RecordPath, ".zip");
        string[] attachments = _screenshotPath.Length == 0 ? [] : [_screenshotPath];

        try
        {
            var save = await Task.Run(() => report.WriteBundle(path, log, InstanceDataRoot.ForMachine().GameSaves, attachments));

            Status = save.Length == 0
                ? strings.Format("Exception.Status.BundleNoSave", path)
                : strings.Format("Exception.Status.Bundle", path, save);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = strings.Format("Exception.Status.Failed", ex.Message);
        }
    }

    // A memory dump of the paused game beside the record, for a mod author with a debugger. Windows
    // writes it from this process; the game is not asked for anything.
    [RelayCommand]
    private async Task SaveDump()
    {
        if (_gameProcessId <= 0)
            return;

        var strings = Strings.Current;
        var path = GameMemoryDump.PathFor(RecordPath);
        var processId = _gameProcessId;

        try
        {
            var bytes = await Task.Run(() => GameMemoryDump.Write(processId, path));

            _openTarget = path;
            Status = strings.Format("Exception.Status.Dump", path, Math.Max(1, bytes / (1024 * 1024)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Status = strings.Format("Exception.Status.Failed", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{_openTarget}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = Strings.Current.Format("Exception.Status.Failed", ex.Message);
        }
    }
}
