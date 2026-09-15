namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class ForensicsPage : Page
    {
        public ForensicsPage()
        {
            InitializeComponent();
        }

        public ForensicsViewModel ViewModel => ShellViewModels.Instance.Forensics;

        private void OnOpenCrashReports(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            Frame?.Navigate(typeof(DiagnosticsPage));
        }

        private void OnOpenLogs(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            Frame?.Navigate(typeof(DiagnosticLogsPage));
        }

        private void OnOpenWatch(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            Frame?.Navigate(typeof(WatchPage));
        }

        private void OnOpenBootChecks(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            Frame?.Navigate(typeof(BootChecksPage));
        }

        private void OnOpenDiagnosticFiles(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            Frame?.Navigate(typeof(DiagnosticFilesPage));
        }
    }
}
