using System.Collections.ObjectModel;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Saves;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.ViewModels
{
    // What the profile in force launches with: argument presets, of which one is chosen, and programs to
    // run beside the game. The profile in force is the last one restored. The chosen preset's arguments
    // join every launch through LaunchArguments, so each way of starting the game carries them, and a
    // program is checked (it exists, its folder exists) before it is started.
    public partial class EnvironmentViewModel
    {
        private ProfileLaunchStore ProfileLaunches => new(ProfileLaunchStore.DefaultPath(ActiveDataRoot));

        private string? LaunchProfile => new ProfileSavesStore(ProfileSavesStore.DefaultRoot(ActiveDataRoot)).ActiveName;

        private bool refreshingProfileLaunch;

        // The typed arguments with the in-force profile's preset after them.
        private string ProfileLaunchArguments(string pageArguments) =>
            LaunchProfile is { } name ? ProfileLaunches.Get(name).Combine(pageArguments) : pageArguments;

        public ObservableCollection<string> LaunchPresetNames { get; } = [];

        public ObservableCollection<LaunchToolRowViewModel> LaunchTools { get; } = [];

        [ObservableProperty]
        public partial string LaunchProfileLabel { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string? SelectedLaunchPreset { get; set; }

        [ObservableProperty]
        public partial string NewLaunchPresetName { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NewLaunchPresetArguments { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NewLaunchToolName { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NewLaunchToolPath { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NewLaunchToolArguments { get; set; } = string.Empty;

        public int MaxLaunchNameLength => CustomExecutable.MaxNameLength;

        public int MaxLaunchArgumentsLength => CustomExecutable.MaxArgumentsLength;

        public void RefreshProfileLaunch()
        {
            refreshingProfileLaunch = true;

            try
            {
                LaunchPresetNames.Clear();
                LaunchTools.Clear();

                if (LaunchProfile is not { } name)
                {
                    LaunchProfileLabel = Strings.Current["Environment.ProfileLaunch.NoProfile"];
                    SelectedLaunchPreset = null;
                    return;
                }

                var launch = ProfileLaunches.Get(name);

                LaunchProfileLabel = Strings.Current.Format("Environment.ProfileLaunch.For", name);

                foreach (var preset in launch.Presets)
                    LaunchPresetNames.Add(preset.Name);

                SelectedLaunchPreset = launch.ActivePreset;

                foreach (var tool in launch.Executables)
                    LaunchTools.Add(new LaunchToolRowViewModel(tool, RunLaunchTool, RemoveLaunchTool));
            }
            finally
            {
                refreshingProfileLaunch = false;
            }
        }

        partial void OnSelectedLaunchPresetChanged(string? value)
        {
            if (refreshingProfileLaunch || LaunchProfile is not { } name)
                return;

            var store = ProfileLaunches;

            if (store.Set(name, store.Get(name) with { ActivePreset = value }))
            {
                StatusMessage = value is null
                    ? Strings.Current.Format("Environment.ProfileLaunch.PresetCleared", name)
                    : Strings.Current.Format("Environment.ProfileLaunch.PresetChosen", value, name);
            }
        }

        [RelayCommand]
        private void ClearLaunchPreset() => SelectedLaunchPreset = null;

        [RelayCommand]
        private void AddLaunchPreset()
        {
            if (LaunchProfile is not { } name || string.IsNullOrWhiteSpace(NewLaunchPresetName))
            {
                StatusMessage = Strings.Current["Environment.ProfileLaunch.PresetNeedsName"];
                return;
            }

            var store = ProfileLaunches;
            var launch = store.Get(name);
            var preset = new LaunchArgumentPreset(NewLaunchPresetName.Trim(), NewLaunchPresetArguments.Trim());

            var presets = launch.Presets
                .Where(p => !string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase))
                .Append(preset)
                .ToList();

            if (presets.Count > ProfileLaunchStore.MaxPresets)
            {
                StatusMessage = Strings.Current.Format("Environment.ProfileLaunch.TooManyPresets", ProfileLaunchStore.MaxPresets);
                return;
            }

            store.Set(name, launch with { Presets = presets });
            NewLaunchPresetName = string.Empty;
            NewLaunchPresetArguments = string.Empty;
            RefreshProfileLaunch();
            StatusMessage = Strings.Current.Format("Environment.ProfileLaunch.PresetSaved", preset.Name);
        }

        [RelayCommand]
        private void RemoveLaunchPreset()
        {
            if (LaunchProfile is not { } name || SelectedLaunchPreset is not { } chosen)
                return;

            var store = ProfileLaunches;
            var launch = store.Get(name);

            store.Set(name, launch with
            {
                Presets = [.. launch.Presets.Where(p => !string.Equals(p.Name, chosen, StringComparison.OrdinalIgnoreCase))],
                ActivePreset = null
            });

            RefreshProfileLaunch();
        }

        [RelayCommand]
        private void AddLaunchTool()
        {
            if (LaunchProfile is not { } name)
                return;

            var tool = new CustomExecutable(NewLaunchToolName.Trim(), NewLaunchToolPath.Trim().Trim('"'), NewLaunchToolArguments.Trim());

            if (tool.Check() is { } problem)
            {
                StatusMessage = Strings.Current.Format("Environment.ProfileLaunch.ToolRefused", problem);
                return;
            }

            var store = ProfileLaunches;
            var launch = store.Get(name);
            var tools = launch.Executables
                .Where(e => !string.Equals(e.Name, tool.Name, StringComparison.OrdinalIgnoreCase))
                .Append(tool)
                .ToList();

            if (tools.Count > ProfileLaunchStore.MaxExecutables)
            {
                StatusMessage = Strings.Current.Format("Environment.ProfileLaunch.TooManyTools", ProfileLaunchStore.MaxExecutables);
                return;
            }

            store.Set(name, launch with { Executables = tools });
            NewLaunchToolName = string.Empty;
            NewLaunchToolPath = string.Empty;
            NewLaunchToolArguments = string.Empty;
            RefreshProfileLaunch();
            StatusMessage = Strings.Current.Format("Environment.ProfileLaunch.ToolSaved", tool.Name);
        }

        private void RemoveLaunchTool(LaunchToolRowViewModel row)
        {
            if (LaunchProfile is not { } name)
                return;

            var store = ProfileLaunches;
            var launch = store.Get(name);

            store.Set(name, launch with
            {
                Executables = [.. launch.Executables.Where(e => !string.Equals(e.Name, row.Name, StringComparison.OrdinalIgnoreCase))]
            });

            RefreshProfileLaunch();
        }

        // Checked at the moment of the press, not when it was saved: the file may have moved since.
        private void RunLaunchTool(LaunchToolRowViewModel row)
        {
            if (row.Executable.Check() is { } problem)
            {
                StatusMessage = Strings.Current.Format("Environment.ProfileLaunch.ToolCannotStart", row.Name, problem);
                row.Refresh();
                return;
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo(row.Executable.Path, row.Executable.Arguments)
            {
                WorkingDirectory = row.Executable.EffectiveWorkingDirectory,
                UseShellExecute = false
            };

            try
            {
                using var process = System.Diagnostics.Process.Start(startInfo);
                StatusMessage = Strings.Current.Format("Environment.ProfileLaunch.ToolStarted", row.Name);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                LoggingService.LogException(ex, "Failed to start a profile tool");
                StatusMessage = Strings.Current.Format("Environment.ProfileLaunch.ToolCannotStart", row.Name, ex.Message);
            }
        }
    }

    public sealed partial class LaunchToolRowViewModel : ObservableObject
    {
        private readonly Action<LaunchToolRowViewModel> run;

        private readonly Action<LaunchToolRowViewModel> remove;

        public LaunchToolRowViewModel(CustomExecutable executable, Action<LaunchToolRowViewModel> run, Action<LaunchToolRowViewModel> remove)
        {
            Executable = executable;
            this.run = run;
            this.remove = remove;
        }

        public CustomExecutable Executable { get; }

        public string Name => Executable.Name;

        public string PathText => Executable.Path;

        // Empty when it can start now, otherwise why not.
        public string ProblemText => Executable.Check() ?? string.Empty;

        public void Refresh() => OnPropertyChanged(nameof(ProblemText));

        [RelayCommand]
        private void Run() => run(this);

        [RelayCommand]
        private void Remove() => remove(this);
    }
}
