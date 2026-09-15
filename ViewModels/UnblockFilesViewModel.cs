using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Services;
using System.Collections.ObjectModel;
using Windows.Storage.Pickers;
using WinRT.Interop;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BannerlordEnvironmentManager.ViewModels
{
    public partial class UnblockFilesViewModel : BaseViewModel
    {
        private sealed class UnblockSettings
        {
            public string SelectedPath { get; set; } = string.Empty;
        }

        [JsonSourceGenerationOptions(WriteIndented = true)]
        [JsonSerializable(typeof(UnblockSettings))]
        private sealed partial class UnblockSettingsJsonContext : JsonSerializerContext
        {
        }

        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bannerlord Environment Manager",
            "unblock_settings.json");

        [ObservableProperty]
        public partial string SelectedPath { get; set; }

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        [ObservableProperty]
        public partial string StatusMessage { get; set; }

        [ObservableProperty]
        public partial int ProgressCurrent { get; set; }

        [ObservableProperty]
        public partial int ProgressTotal { get; set; }

        [ObservableProperty]
        public partial string? CurrentFile { get; set; }

        [ObservableProperty]
        public partial bool ShowProgress { get; set; }

        public ObservableCollection<UnblockResultItem> Results { get; } = new();

        public UnblockFilesViewModel()
        {
            Title = "Unblock Files";
            SelectedPath = string.Empty;
            StatusMessage = Strings.Current["Unblock.Status.Initial"];
            LoadSettings();
            PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SelectedPath))
                {
                    SaveSettings();
                }
            };
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize(json, UnblockSettingsJsonContext.Default.UnblockSettings);
                    if (settings != null)
                    {
                        SelectedPath = settings.SelectedPath;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load unblock settings: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            try
            {
                // Only a folder is worth remembering. A remembered list of files would come back next
                // launch as text in the box that nothing acts on, which is the trap this page had.
                var settings = new UnblockSettings
                {
                    SelectedPath = Directory.Exists(SelectedPath) ? SelectedPath : string.Empty
                };

                var json = JsonSerializer.Serialize(settings, UnblockSettingsJsonContext.Default.UnblockSettings);
                var directory = Path.GetDirectoryName(SettingsFilePath);        
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save unblock settings: {ex.Message}");
            }
        }

        [RelayCommand(CanExecute = nameof(CanExecuteCommands))]
        private async Task BrowseFolderAsync()
        {
            var picker = new FolderPicker();
            if (!TryInitializePicker(picker))
            {
                StatusMessage = Strings.Current["Unblock.Picker.FolderError"];
                return;
            }

            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();

            if (folder == null)
            {
                StatusMessage = Strings.Current["Unblock.Picker.FolderCanceled"];
                return;
            }

            // The file selection has to go with it. Leaving it set meant the box showed a folder and
            // Unblock acted on files picked earlier, which is the one thing the display rules out.
            _selectedFiles = null;
            SelectedPath = folder.Path;
            StatusMessage = Strings.Current.Format("Unblock.Picker.FolderSelected", folder.Path);
        }

        [RelayCommand(CanExecute = nameof(CanExecuteCommands))]
        private async Task BrowseFilesAsync()
        {
            var picker = new FileOpenPicker();
            if (!TryInitializePicker(picker))
            {
                StatusMessage = Strings.Current["Unblock.Picker.FileError"];
                return;
            }

            picker.FileTypeFilter.Add("*");
            var files = await picker.PickMultipleFilesAsync();

            if (files == null || files.Count == 0)
            {
                StatusMessage = Strings.Current["Unblock.Picker.FilesCanceled"];
                return;
            }

            _selectedFiles = [.. files.Select(f => f.Path)];

            // The full paths, because this is what Unblock will act on and a list of bare file names
            // does not say which folder they came from.
            SelectedPath = string.Join("; ", _selectedFiles);
            StatusMessage = Strings.Current.Plural("Unblock.Picker.FilesSelected", files.Count);
        }

        private List<string>? _selectedFiles;

        [RelayCommand(CanExecute = nameof(CanExecuteCommands))]
        private async Task UnblockAsync()
        {
            LoggingService.Log($"Starting unblock process for path: {SelectedPath}");
            Results.Clear();
            IsBusy = true;
            ShowProgress = true;
            ProgressCurrent = 0;
            ProgressTotal = 0;

            try
            {
                FileUnblocker.UnblockStatistics stats;
                List<FileUnblocker.UnblockResult> results;

                var progress = new Progress<(int current, int total, string currentFile)>(report =>
                {
                    App.AppWindow?.DispatcherQueue.TryEnqueue(() =>
                    {
                        ProgressCurrent = report.current;
                        ProgressTotal = report.total;
                        CurrentFile = report.currentFile;
                    });
                });

                if (_selectedFiles != null && _selectedFiles.Count > 0)
                {
                    LoggingService.Log($"Unblocking {_selectedFiles.Count} explicitly selected files.");
                    // Unblock selected files
                    (stats, results) = await FileUnblocker.UnblockFilesAsync(_selectedFiles, progress);
                }
                else if (!string.IsNullOrWhiteSpace(SelectedPath) && Directory.Exists(SelectedPath))
                {
                    LoggingService.Log($"Unblocking directory: {SelectedPath}");
                    // Unblock folder
                    (stats, results) = await FileUnblocker.UnblockDirectoryAsync(SelectedPath, progress);
                }
                else
                {
                    LoggingService.Log("No valid path or files selected for unblocking.", LogLevel.Warn);
                    StatusMessage = Strings.Current["Unblock.Status.NoSelection"];
                    return;
                }

                LoggingService.Log($"Unblock complete. Total: {stats.TotalFiles}, Unblocked: {stats.Unblocked}, Already Clean: {stats.AlreadyUnblocked}, Not checked: {stats.Unknown}, Failed: {stats.Failed}");

                foreach (var result in results.Where(r => r.Status != FileUnblocker.UnblockStatus.AlreadyUnblocked))
                {
                    Results.Add(new UnblockResultItem(
                        Path.GetFileName(result.FilePath),
                        result.FilePath,
                        Describe(result.Status),
                        result.ErrorMessage
                    ));

                    if (result.Status == FileUnblocker.UnblockStatus.Failed)
                    {
                        LoggingService.Log($"Failed to unblock {result.FilePath}: {result.ErrorMessage}", LogLevel.Error);
                    }
                }

                // A file BEM could not look at is not a file that turned out to be clean, so it is counted
                // on its own and the count of clean ones covers only the ones that were read.
                StatusMessage = Strings.Current.Format("Unblock.Complete.Head", stats.Unblocked, stats.AlreadyUnblocked) +
                    (stats.Unknown == 0 ? string.Empty : Strings.Current.Format("Unblock.Complete.Unknown", stats.Unknown)) +
                    Strings.Current.Format("Unblock.Complete.Tail", stats.Failed, stats.TotalFiles);
            }
            catch (Exception ex)
            {
                StatusMessage = Strings.Current.Format("Unblock.Error", ex.Message);
                LoggingService.LogException(ex, "Error during unblock process");
            }
            finally
            {
                IsBusy = false;
                ShowProgress = false;
                CurrentFile = null;
            }
        }

        private static string Describe(FileUnblocker.UnblockStatus status) => status switch
        {
            FileUnblocker.UnblockStatus.Unblocked => Strings.Current["Unblock.Status.Item.Unblocked"],
            FileUnblocker.UnblockStatus.AlreadyUnblocked => Strings.Current["Unblock.Status.Item.AlreadyClean"],
            FileUnblocker.UnblockStatus.Unknown => Strings.Current["Unblock.Status.Item.Unknown"],
            _ => Strings.Current["Unblock.Status.Item.Failed"]
        };

        private bool CanExecuteCommands() => !IsBusy;

        private static bool TryInitializePicker(object picker)
        {
            var window = App.AppWindow;
            if (window is null)
                return false;

            var hwnd = WindowNative.GetWindowHandle(window);

            switch (picker)
            {
                case FileOpenPicker openPicker:
                    InitializeWithWindow.Initialize(openPicker, hwnd);
                    return true;

                case FolderPicker folderPicker:
                    InitializeWithWindow.Initialize(folderPicker, hwnd);
                    return true;

                default:
                    return false;
            }
        }
    }

    public sealed record UnblockResultItem(string FileName, string FullPath, string Status, string? ErrorMessage)
    {
        // A list item with no automation name of its own is announced by its ToString, and a record's
        // own ToString reads the type name and every member aloud.
        public override string ToString() => $"{FileName}, {Status}";
    }
}




