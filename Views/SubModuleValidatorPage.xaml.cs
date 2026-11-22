using BannerlordEnvironmentManager.Core.Modules;
using Microsoft.UI.Xaml.Navigation;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class SubModuleValidatorPage : Page
    {
        public SubModuleValidatorPage()
        {
            InitializeComponent();

#if DEV_BEM
            DevRing.DeveloperSurfaces.AddTo(this);
#endif
        }

        public SubModuleValidatorViewModel ValidatorViewModel => ShellViewModels.Instance.Validator;

        // The folder this page bulk-edits has to be the version the user is playing. It used to come
        // from a machine-wide validator_settings.json, so opening the validator with one version
        // selected listed another version's modules and "Update all versions" rewrote SubModule.xml
        // in the install nobody was looking at. The remembered path stays as the fallback for a
        // folder picked by hand on a machine with no install configured at all.
        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var environment = ShellViewModels.Instance.Environment;

            if (environment.Modules.Count == 0)
                await environment.Refresh();

            if (!string.IsNullOrWhiteSpace(environment.GameInstallPath))
                ValidatorViewModel.FollowInstance(ModuleScanner.GetModulesFolder(environment.GameInstallPath));
        }
    }
}
