namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class DiagnosticLogsPage : Page
    {
        public DiagnosticLogsPage()
        {
            InitializeComponent();
        }

        public DiagnosticsViewModel DiagnosticsViewModel => ShellViewModels.Instance.Diagnostics;

        // The row records itself first, and this is the half that cannot be skipped. ContextRequested
        // bubbles, so a handler on the list runs only if nothing between the click and the list marked
        // the event handled, while the framework shows the flyout regardless.
        private void OnLogRowContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = e;

            DiagnosticsViewModel.SetContextLog((sender as FrameworkElement)?.DataContext as LogRowViewModel);
        }

        // The list-level handler is what clears the row when the click landed on the empty space below
        // the last one, where no row handler runs and every item graying out is the right answer.
        private void OnLogContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = sender;

            DiagnosticsViewModel.SetContextLog(ContextRow.Resolve<LogRowViewModel>(e.OriginalSource));
        }
    }
}
