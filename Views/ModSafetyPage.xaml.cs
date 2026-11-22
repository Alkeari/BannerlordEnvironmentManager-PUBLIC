using Microsoft.UI.Xaml.Navigation;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class ModSafetyPage : Page
    {
        public ModSafetyPage()
        {
            InitializeComponent();
        }

        public ModSafetyViewModel SafetyViewModel => ShellViewModels.Instance.ModSafety;

        // The startup scan has usually finished by the time this page is opened, so opening it shows
        // the result rather than starting again. It only starts one when the startup scan is on and
        // has somehow not run at all, which is what stops an empty page reading as "nothing found".
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            SafetyViewModel.OnOpened();
        }
    }
}
