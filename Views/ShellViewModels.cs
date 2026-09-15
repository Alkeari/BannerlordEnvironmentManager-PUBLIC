using System.Text.Json;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.Views
{
    public sealed class ShellViewModels
    {
        public static ShellViewModels Instance { get; } = new();

        public ShellNavigationSettings NavigationSettings { get; } = new();

        public InstallViewModel Install { get; } = new();

        public UnblockFilesViewModel Unblock { get; } = new();

        public SubModuleValidatorViewModel Validator { get; } = new();

        public EnvironmentViewModel Environment { get; } = new();

        public VersionsViewModel Versions { get; } = new();

        // The version dropdown on Play and the instance list on Versions are two views of one thing,
        // so they share one view model rather than each keeping their own idea of what is installed.
        public VersionSwitcherViewModel VersionSwitcher { get; } = new();

        public SavesViewModel Saves { get; } = new();

        public DiagnosticsViewModel Diagnostics { get; } = new();

        public ModSafetyViewModel ModSafety { get; } = new();

        public HealthViewModel Health { get; }

        public ForensicsViewModel Forensics { get; }

        public ShellViewModels()
        {
            Health = new HealthViewModel(Diagnostics, ModSafety);
            Forensics = new ForensicsViewModel(Diagnostics);

            VersionSwitcher.ActiveInstanceChanged += OnActiveInstanceChanged;

            // Fire-and-forget: filling the dropdown reads the instance folders and fetches Steam's
            // branch list, and Play must not wait on either to open.
            _ = VersionSwitcher.RefreshAsync();
        }

        // The active instance decides which install every page reads, so a switch reloads the load
        // order and the instance list rather than leaving both describing the version that was
        // playing a moment ago.
        private async void OnActiveInstanceChanged(object? sender, EventArgs e)
        {
            try
            {
                await Environment.Refresh();
                await Versions.RefreshCommand.ExecuteAsync(null);

                // Every page that reads game state resolves the data root at the moment it scans, so
                // these are correct the next time they run. The ones that are cheap to run again are
                // run now, because a page still showing the version that was selected a moment ago
                // reads as BEM ignoring the switch. The log and crash sweep is not among them: it
                // walks the whole install and is left for the user to ask for.
                Saves.Refresh();
                ModSettings.RefreshCommand.Execute(null);
                Diagnostics.RefreshAcceptedFindingRows();

                // Everything else that was read off the other version is dropped rather than read
                // again. Mod Safety's alarms, the install checks the Health badge counts, and the
                // crash reports and logs on Diagnostics were all computed against modules the user is
                // no longer on, and Health went on showing those counts under a header naming this
                // version. An empty list saying to press Scan is the honest answer; rescanning here
                // would walk the whole install on every switch for a result nobody asked for.
                ModSafety.ClearForVersionChange();
                Diagnostics.ClearForVersionChange();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LoggingService.LogException(ex, "Failed to reload the pages after the version changed");
            }
        }

        public ModSettingsViewModel ModSettings { get; } = new();

        public WatchSessionViewModel Watch { get; } = new();

        // Built on first use rather than at startup: finding winget means walking WindowsApps, and a
        // session that never opens Settings should not pay for that.
        public ToolkitViewModel Toolkit => toolkit.Value;

        private readonly Lazy<ToolkitViewModel> toolkit = new(() => new ToolkitViewModel());
    }

// How tall a module row is. Compact is for the 32px ceiling, Standard is the intended 40px home,
// Comfortable is the 48px one. Stored as a string so a settings file written by an older build,
// which has no value, reads as Standard rather than Compact.
public enum RowDensity
{
    Compact,
    Standard,
    Comfortable,
}

// The Advanced group starts collapsed, which would cost a click every single session to anyone
// who actually uses the pages behind it. Written on each toggle rather than at shutdown, because
// the app can be closed without an orderly exit.
public sealed class ShellNavigationSettings : ObservableObject
{
    private static readonly string FilePath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager",
        "shell-settings.json");

    // Nullable rather than a bool, because a settings file written before Diagnostics had a group
    // has no value for it and reading a missing field as false would collapse it on first run.
    private sealed record Stored(
        bool IsAdvancedExpanded,
        bool? IsDiagnosticsExpanded = null,
        bool? IsAdvancedMode = null,
        string? RowDensity = null);

    public ShellNavigationSettings() => Load();

    public bool IsAdvancedExpanded { get; private set; }

    // Ships expanded. The pages under Diagnostics are the ones a crash sends you to, and defaulting
    // them shut buries the value behind a click for no gain.
    public bool IsDiagnosticsExpanded { get; private set; } = true;

    // Whether the pages and panels that exist for a power user are shown. Presentation only: it
    // disables no feature and hides nothing that is doing work, which is why it can default off
    // without falling foul of the rule that a useful capability ships on.
    //
    // Observable because gating happens per page and a page already on screen has to react. The
    // group-expanded flags above are read once when the pane is built and never change after, which
    // is why they were fine as plain properties and this one is not.
    private bool isAdvancedMode;

    public bool IsAdvancedMode
    {
        get => isAdvancedMode;
        private set => SetProperty(ref isAdvancedMode, value);
    }

    private RowDensity rowDensity = RowDensity.Standard;

    public RowDensity RowDensity
    {
        get => rowDensity;
        private set => SetProperty(ref rowDensity, value);
    }

    public void SetAdvancedMode(bool advanced)
    {
        if (IsAdvancedMode == advanced)
            return;

        IsAdvancedMode = advanced;
        Save();
    }

    public void SetRowDensity(RowDensity density)
    {
        if (RowDensity == density)
            return;

        RowDensity = density;
        Save();
    }

        public void SetAdvancedExpanded(bool expanded)
        {
            if (IsAdvancedExpanded == expanded)
                return;

            IsAdvancedExpanded = expanded;
            Save();
        }

        public void SetDiagnosticsExpanded(bool expanded)
        {
            if (IsDiagnosticsExpanded == expanded)
                return;

            IsDiagnosticsExpanded = expanded;
            Save();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return;

                if (JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath)) is { } stored)
                {
                    IsAdvancedExpanded = stored.IsAdvancedExpanded;
                    IsDiagnosticsExpanded = stored.IsDiagnosticsExpanded ?? true;
                    IsAdvancedMode = stored.IsAdvancedMode ?? false;
                    RowDensity = ParseDensity(stored.RowDensity);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to load the shell navigation settings");
            }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(
                    FilePath, JsonSerializer.Serialize(
                        new Stored(IsAdvancedExpanded, IsDiagnosticsExpanded, IsAdvancedMode, RowDensity.ToString())));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to save the shell navigation settings");
            }
        }

        private static RowDensity ParseDensity(string? text) =>
            Enum.TryParse<RowDensity>(text, out var density) ? density : RowDensity.Standard;
    }
}
