using System.Text.Json;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Services;
using BannerlordEnvironmentManager.Core.Instances;

namespace BannerlordEnvironmentManager.Views
{
    public sealed class ShellViewModels
    {
        public static ShellViewModels Instance { get; } = new();

        public ShellNavigationSettings NavigationSettings { get; } = new();

        public InstallViewModel Install { get; } = new();


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


        public ShellViewModels()
        {
            Health = new HealthViewModel(Diagnostics, ModSafety, Environment);
            Environment.Library = Install;

            // A module's own findings show on its row on Play.
            Health.ModuleFindingsChanged += (_, _) => Environment.SetModuleFindings(Health.ModuleFindings());

            VersionSwitcher.ActiveInstanceChanged += OnActiveInstanceChanged;

            // Health is live: its checks run once the load order is read (startup and a version
            // switch) and again shortly after any change to the order, with nothing to press.
            Environment.LoadOrderChanged += (_, _) => Health.ScheduleChecks();
            Environment.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EnvironmentViewModel.IsLoaded) && Environment.IsLoaded)
                    Health.ScheduleChecks();
            };

            // Fire-and-forget: filling the dropdown reads the instance folders and fetches Steam's
            // branch list, and Play must not wait on either to open.
            // Started after the constructor returns (the Yield), because the refresh adopts the current
            // install through ShellViewModels.Instance, which is not assigned until then.
            _ = RefreshVersionsWhenReadyAsync().ContinueWith(
                task => LoggingService.LogException(task.Exception!.GetBaseException(), "Version list refresh failed"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        private async Task RefreshVersionsWhenReadyAsync()
        {
            await Task.Yield();
            await VersionSwitcher.RefreshAsync();
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
    private static string FilePath => Path.Combine(AppDataRoot.Folder, "shell-settings.json");

    // Nullable rather than a bool, because a settings file written before Diagnostics had a group
    // has no value for it and reading a missing field as false would collapse it on first run.
    private sealed record Stored(
        bool IsAdvancedExpanded,
        bool? IsDiagnosticsExpanded = null,
        bool? IsAdvancedMode = null,
        string? RowDensity = null,
        string? LastPage = null,
        bool? WalkthroughDone = null);

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

    // The density a fresh install starts at. Compact, per the redesign's recommendation (proposal 1.3
    // and decision 6.2, not yet answered by the owner): one constant, so flipping back to Standard is
    // this line alone. A settings file that already names a density keeps it.
    public const RowDensity DefaultRowDensity = RowDensity.Compact;

    private RowDensity rowDensity = DefaultRowDensity;

    public RowDensity RowDensity
    {
        get => rowDensity;
        private set => SetProperty(ref rowDensity, value);
    }

    // The destination the user was last on, by its navigation tag, so BEM reopens there.
    public string? LastPage { get; private set; }

    // Whether the first-run guide has been through. A settings file written before the guide existed
    // belongs to someone already using BEM, so a missing value there reads as done; only a machine
    // with no settings file at all starts with the guide.
    public bool WalkthroughDone { get; private set; }

    // Raised by Settings' "Run the setup guide again", which the shell answers.
    public event EventHandler? WalkthroughRequested;

    public void RequestWalkthrough() => WalkthroughRequested?.Invoke(this, EventArgs.Empty);

    public void SetWalkthroughDone()
    {
        if (WalkthroughDone)
            return;

        WalkthroughDone = true;
        Save();
    }

    public void SetLastPage(string tag)
    {
        if (LastPage == tag)
            return;

        LastPage = tag;
        Save();
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

                WalkthroughDone = true;

                if (JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath)) is { } stored)
                {
                    IsAdvancedExpanded = stored.IsAdvancedExpanded;
                    IsDiagnosticsExpanded = stored.IsDiagnosticsExpanded ?? true;
                    IsAdvancedMode = stored.IsAdvancedMode ?? false;
                    RowDensity = ParseDensity(stored.RowDensity);
                    LastPage = stored.LastPage;
                    WalkthroughDone = stored.WalkthroughDone ?? true;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Failed to load the shell navigation settings");
            }
        }

        // Written on a thread of its own: the dump of 2026-11-21 21:27 caught the UI thread in
        // File.WriteAllText from here during a page change. The text is built now, so a later change
        // cannot alter what an earlier write holds, and the writes keep their order.
        private static readonly BackgroundWriter Writer = new("BEM shell settings writer", ex => LoggingService.LogException(ex, "Failed to save the shell navigation settings"));

        private void Save()
        {
            var path = FilePath;
            var json = JsonSerializer.Serialize(
                new Stored(IsAdvancedExpanded, IsDiagnosticsExpanded, IsAdvancedMode, RowDensity.ToString(), LastPage, WalkthroughDone));

            Writer.Enqueue(() =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, json);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LoggingService.LogException(ex, "Failed to save the shell navigation settings");
                }
            });
        }

        private static RowDensity ParseDensity(string? text) =>
            Enum.TryParse<RowDensity>(text, out var density) ? density : DefaultRowDensity;
    }
}
