using BannerlordEnvironmentManager.Core.Localization;
using Microsoft.UI.Xaml.Navigation;

namespace BannerlordEnvironmentManager.Views
{
    // The one destination for "what's wrong?": Problems (the checks and the SubModule.xml Validator),
    // Crashes (the crash evidence), Find the Culprit (the bisection), Mod Settings (every settings folder
    // at once; one mod's own is a button on its row) and Tools (the optional helper programs). The pages that used to be destinations of their own are shown
    // inside it, and anything that navigates to one of them lands here with that view open (PageHost).
    public sealed partial class TroubleshootPage : Page, ShellPage.IHostsViews
    {
        private const string ProblemsTab = "Problems";
        private const string CrashesTab = "Crashes";
        private const string CulpritTab = "Culprit";

        // Find the Culprit is a view inside the Crashes tab, not a tab of its own, and Mod Settings is
        // reached from a mod's own button, so neither has a chip in the row. These say which is open.
        private bool culpritOpen;
        private bool settingKey;
        private const string ModSettingsTab = "ModSettings";
        private const string ToolsTab = "Tools";

        public TroubleshootPage()
        {
            InitializeComponent();

            Tabs.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<Controls.SegmentedItem>
            {
                new(ProblemsTab, Strings.Current["Troubleshoot.Tab.Problems"], Strings.Current["Troubleshoot.Tab.Problems.Tooltip"]),
                new(CrashesTab, Strings.Current["Troubleshoot.Tab.Crashes"], Strings.Current["Troubleshoot.Tab.Crashes.Tooltip"]),
                new(ToolsTab, Strings.Current["Troubleshoot.Tab.Tools"], Strings.Current["Troubleshoot.Tab.Tools.Tooltip"]),
            };

            Tabs.SelectedKey = ProblemsTab;
            ApplyTab(null);

            ShellViewModels.Instance.NavigationSettings.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ShellNavigationSettings.IsAdvancedMode))
                    DispatcherQueue.TryEnqueue(ApplyTabRow);
            };
        }

        // Basic shows Problems alone, with no tab row: Crashes and Find the Culprit are Advanced. A
        // crash that opens Crashes still opens it, and the row then shows so the way back is there.
        private void ApplyTabRow() =>
            Tabs.Visibility = ShellViewModels.Instance.NavigationSettings.IsAdvancedMode || Tabs.SelectedKey != ProblemsTab
                ? Visibility.Visible
                : Visibility.Collapsed;

        public HealthViewModel ViewModel => ShellViewModels.Instance.Health;

        // The one Scan on the Crashes tab reads the same Diagnostics view model the evidence pages show.
        public DiagnosticsViewModel Diagnostics => ShellViewModels.Instance.Diagnostics;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Read the Modules folder again on every visit, so a fix made outside BEM (a folder removed
            // by hand) clears its row the next time this page is opened, not the next time BEM starts.
            _ = Diagnostics.CheckInstallCommand.ExecuteAsync(null);

            if (e.Parameter is string view)
                ShowView(view);
        }

        // Opened from anywhere in BEM by naming a view (PageHost): a check's name opens Problems on it,
        // an evidence name opens Crashes on it, and "Culprit" opens the bisection.
        public void ShowView(string view)
        {
            if (string.IsNullOrEmpty(view))
                return;

            var tab = view switch
            {
                CulpritTab => CrashesTab,
                ModSettingsTab => ModSettingsTab,
                ToolsTab => ToolsTab,
                "Crash" or "Reports" or "Logs" or "Files" or "Watch" => CrashesTab,
                _ => ProblemsTab,
            };

            culpritOpen = view == CulpritTab;
            SelectKey(tab);
            ApplyTab(view);
        }

        // Sets the chip without the change counting as the user choosing a tab.
        private void SelectKey(string key)
        {
            settingKey = true;

            try
            {
                Tabs.SelectedKey = key;
            }
            finally
            {
                settingKey = false;
            }
        }

        private void OnOpenCulprit(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            culpritOpen = true;
            ApplyTab(null);
        }

        private void OnBackToCrashes(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            culpritOpen = false;
            ApplyTab(null);
        }

        // One Scan for every tab: the install checks, then the log and crash report scan.
        private void OnScanAll(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            if (ViewModel.RunAllChecksCommand.CanExecute(null))
                ViewModel.RunAllChecksCommand.Execute(null);
            if (Diagnostics.ScanLogsAndCrashReportsCommand.CanExecute(null))
                Diagnostics.ScanLogsAndCrashReportsCommand.Execute(null);
        }

        private void OnTabChanged(Controls.Segmented sender, string key)
        {
            _ = sender;
            _ = key;

            if (!settingKey)
                culpritOpen = false;

            ApplyTab(null);
        }

        private void ApplyTab(string? view)
        {
            var tab = Tabs.SelectedKey;
            var culprit = culpritOpen && tab == CrashesTab;
            CulpritOpenButton.Visibility = tab == CrashesTab && !culprit ? Visibility.Visible : Visibility.Collapsed;
            CulpritBackButton.Visibility = culprit ? Visibility.Visible : Visibility.Collapsed;

            ApplyTabRow();

            var scans = tab == ProblemsTab || (tab == CrashesTab && !culprit);
            ScanButton.Visibility = scans ? Visibility.Visible : Visibility.Collapsed;
            ScanRing.Visibility = scans ? Visibility.Visible : Visibility.Collapsed;

            TabFrame.Visibility = culprit ? Visibility.Collapsed : Visibility.Visible;
            CulpritCard.Visibility = culprit ? Visibility.Visible : Visibility.Collapsed;

            if (culprit)
            {
                PageHost.Show(CulpritFrame, typeof(BootChecksPage));
                return;
            }

            var page = tab switch
            {
                CrashesTab => typeof(CrashesPage),
                ModSettingsTab => typeof(ModSettingsPage),
                ToolsTab => typeof(ToolkitPage),
                _ => typeof(ProblemsPage),
            };
            PageHost.Show(TabFrame, page, view);

            // A page already on screen is not navigated again, so it is told directly.
            if (view is not null && TabFrame.Content is ShellPage.IHostsViews host)
                host.ShowView(view);
        }
    }
}
