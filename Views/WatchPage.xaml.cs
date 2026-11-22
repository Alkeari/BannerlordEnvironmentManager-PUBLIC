using Microsoft.UI.Xaml.Navigation;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class WatchPage : Page
    {
        public WatchPage()
        {
            InitializeComponent();
        }

        public WatchSessionViewModel Watch => ShellViewModels.Instance.Watch;

        // The companion can be removed from outside BEM, and a watch armed in a previous session
        // outlives this window, so the state is re-read every time the page is opened rather than
        // once at construction.
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Watch.Refresh();
        }
    }
}
