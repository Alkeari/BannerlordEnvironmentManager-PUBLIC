using BannerlordEnvironmentManager.ViewModels;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class ToolkitPage : Page
    {
        public ToolkitPage() => InitializeComponent();

        public ToolkitViewModel ViewModel => ShellViewModels.Instance.Toolkit;

        // Re-probed on every visit rather than only when the view model is first built. A program can be
        // installed or removed outside BEM between two visits, and a row that still says "not installed"
        // over a working copy is worse than no row.
        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            ViewModel.RefreshCommand.Execute(null);
        }
    }
}
