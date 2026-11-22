using BannerlordEnvironmentManager.Core.Localization;
using Microsoft.UI.Xaml.Navigation;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class CrashesPage : Page, ShellPage.IHostsViews
    {
        public CrashesPage()
        {
            InitializeComponent();

            Tabs.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<Controls.SegmentedItem>
            {
                new("Reasoning", Strings.Current["Forensics.Tab.Reasoning"]),
                new("Crash", Strings.Current["Forensics.Tab.Crash"]),
                new("Reports", Strings.Current["Diagnostics.Title"]),
                new("Logs", Strings.Current["Logs.Title"]),
                new("Files", Strings.Current["Files.Title"]),
                new("Watch", Strings.Current["Watch.Title"]),
            };

            Tabs.SelectedKey = "Reasoning";
            ApplyTab();
            ShowReasoning(null);
        }

        // The suspects are ranked by the crash report analysis Diagnostics already runs.
        public DiagnosticsViewModel Diagnostics => ShellViewModels.Instance.Diagnostics;

        public string SuspectCount(int count) => count == 0 ? string.Empty : Strings.Current.Plural("Forensics.Suspects.Count", count);

        public Visibility NoSuspects(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is string view)
                ShowView(view);
        }

        // Opened from anywhere in BEM by naming the evidence (PageHost, through Troubleshoot).
        public void ShowView(string view)
        {
            Tabs.SelectedKey = view;
            ApplyTab();
        }

        private void OnTabChanged(Controls.Segmented sender, string key)
        {
            _ = sender;
            _ = key;
            ApplyTab();
        }

        private void ApplyTab()
        {
            Type? page = Tabs.SelectedKey switch
            {
                "Crash" => typeof(CrashAnalysisPage),
                "Reports" => typeof(DiagnosticsPage),
                "Logs" => typeof(DiagnosticLogsPage),
                "Files" => typeof(DiagnosticFilesPage),
                "Watch" => typeof(WatchPage),
                _ => null,
            };

            ReasoningView.Visibility = page is null ? Visibility.Visible : Visibility.Collapsed;
            EvidenceFrame.Visibility = page is null ? Visibility.Collapsed : Visibility.Visible;

            if (page is not null)
                PageHost.Show(EvidenceFrame, page);
        }

        private void OnSuspectSelected(object sender, SelectionChangedEventArgs e)
        {
            _ = sender;
            _ = e;

            ShowReasoning(SuspectList.SelectedItem as CrashModuleRowViewModel);
            Tabs.SelectedKey = "Reasoning";
            ApplyTab();
        }

        private void ShowReasoning(CrashModuleRowViewModel? suspect)
        {
            ReasoningTitle.Text = suspect is null
                ? Strings.Current["Forensics.Reasoning.None"]
                : Strings.Current.Format("Forensics.Reasoning.Title", suspect.Heading);
            ReasoningEvidence.Text = suspect?.Evidence ?? string.Empty;
            ReasoningNote.Text = suspect?.Note ?? string.Empty;
        }
    }
}
