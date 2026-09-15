using Microsoft.UI.Xaml.Navigation;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class HealthPage : Page
    {
        public HealthPage()
        {
            InitializeComponent();
        }

        public HealthViewModel ViewModel => ShellViewModels.Instance.Health;

        // The accepted list itself lives on Diagnostics: it aggregates every family - install checks,
        // boot checks, mod safety - that writes to the same store, and Health is the one place all of
        // them are already in view together.
        public DiagnosticsViewModel DiagnosticsViewModel => ShellViewModels.Instance.Diagnostics;

        public ShellNavigationSettings Navigation => ShellViewModels.Instance.NavigationSettings;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            DiagnosticsViewModel.RefreshAcceptedFindingRows();
        }

        private void OnForgetAcceptedFindingClicked(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is Button { Tag: AcceptedFindingRowViewModel row })
                row.Forget();
        }

        private void OnOpenInstallChecks(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            Frame?.Navigate(typeof(InstallChecksPage));
        }

        private void OnOpenModOverlaps(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            Frame?.Navigate(typeof(ModOverlapsPage));
        }

        private void OnOpenModSafety(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            Frame?.Navigate(typeof(ModSafetyPage));
        }

        private void OnOpenBootChecks(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            Frame?.Navigate(typeof(BootChecksPage));
        }

        private void OnOpenValidator(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            Frame?.Navigate(typeof(SubModuleValidatorPage));
        }

        private void OnOpenCrashReports(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            Frame?.Navigate(typeof(DiagnosticsPage));
        }

        private void OnRefresh(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            ViewModel.Refresh();
        }
    }
}
