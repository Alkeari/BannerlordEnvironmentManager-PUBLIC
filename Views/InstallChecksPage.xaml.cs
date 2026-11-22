namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class InstallChecksPage : Page
    {
        public InstallChecksPage()
        {
            InitializeComponent();

            Unloaded += OnPageUnloaded;
            DiagnosticsViewModel.EnvironmentTabRequested += OnEnvironmentTabRequested;
        }

        public DiagnosticsViewModel DiagnosticsViewModel => ShellViewModels.Instance.Diagnostics;

        // The known-issue checks read the module folders and nothing else, so they run when this page
        // opens rather than waiting for a crash report to exist.
        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            _ = DiagnosticsViewModel.CheckInstallCommand.ExecuteAsync(null);
        }

        // The view models outlive the page, so a subscription left behind would fire on a page that is
        // no longer in the frame and navigate on top of whatever the user moved to.
        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            Unloaded -= OnPageUnloaded;
            DiagnosticsViewModel.EnvironmentTabRequested -= OnEnvironmentTabRequested;
        }

        private void OnEnvironmentTabRequested() => Frame?.Navigate(typeof(EnvironmentPage));

        private void OnKnownIssueContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = sender;

            DiagnosticsViewModel.SetContextIssue(ContextRow.Resolve<KnownIssueRowViewModel>(e.OriginalSource));
        }

        private void OnAcceptFindingConfirmed(Controls.FindingRow sender, object? item)
        {
            _ = sender;

            if (item is KnownIssueRowViewModel row)
                row.AcceptRisk();
        }

        private void OnUndoFindingRequested(Controls.FindingRow sender, object? item)
        {
            _ = sender;

            if (item is KnownIssueRowViewModel row)
                DiagnosticsViewModel.UndoAcceptFinding(row);
        }
    }
}
