using Microsoft.UI.Xaml.Navigation;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class ModSettingsPage : Page
    {
        public ModSettingsPage()
        {
            InitializeComponent();
        }

        public ModSettingsViewModel ViewModel => ShellViewModels.Instance.ModSettings;

        // Every path on this page resolves through the version being played, and only
        // EnvironmentViewModel.Refresh works out which version that is. Reached before Play has ever
        // run, this page showed the resting version's settings files until the user pressed Refresh
        // themselves.
        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var environment = ShellViewModels.Instance.Environment;

            if (environment.Modules.Count == 0)
                await environment.Refresh();

            ViewModel.RefreshCommand.Execute(null);
        }
    }
}
