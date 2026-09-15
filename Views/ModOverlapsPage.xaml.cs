namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class ModOverlapsPage : Page
    {
        public ModOverlapsPage()
        {
            InitializeComponent();

            Unloaded += OnPageUnloaded;
            DiagnosticsViewModel.EnvironmentTabRequested += OnEnvironmentTabRequested;
        }

        public DiagnosticsViewModel DiagnosticsViewModel => ShellViewModels.Instance.Diagnostics;

        public ShellNavigationSettings Navigation => ShellViewModels.Instance.NavigationSettings;

        // The overlap comparison is part of the same install check, so opening this page fills it the
        // same way opening Install Checks does.
        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            _ = DiagnosticsViewModel.CheckInstallCommand.ExecuteAsync(null);
        }

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
    }
}
