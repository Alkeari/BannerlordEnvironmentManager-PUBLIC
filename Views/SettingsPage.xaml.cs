namespace BannerlordEnvironmentManager.Views
{
    using BannerlordEnvironmentManager.Core.Localization;
    using BannerlordEnvironmentManager.Localization;
    using BannerlordEnvironmentManager.Services;
    using Microsoft.UI.Xaml.Navigation;

    public sealed partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            InitializeComponent();

#if DEV_BEM
            DevRing.DeveloperSurfaces.AddTo(this);
#endif
        }

        public string AppVersionText => $"Version {Services.AppVersion.Display} by Alkeari Labs LLC";

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var index = NavigationSettings.RowDensity switch
            {
                RowDensity.Compact => 0,
                RowDensity.Comfortable => 2,
                _ => 1,
            };

            RowDensityCombo.SelectedIndex = index;

            LanguageCombo.ItemsSource = LocalizationService.Available;
            LanguageCombo.SelectedItem = LocalizationService.Available
                .FirstOrDefault(option => option.Code == LocalizationService.CurrentCode);

            // The Nexus key field lives inside a collapsed expander a long way down the page, and the
            // "add a key" button on Library used to land a user at the top of Settings with no hint
            // where to look. Navigating with this parameter opens the section and brings it into view.
            if (e.Parameter is "NexusKey")
            {
                NetworkFeaturesExpander.IsExpanded = true;
                NetworkFeaturesExpander.StartBringIntoView();
            }
        }

        public InstallViewModel ViewModel => ShellViewModels.Instance.Install;

        public EnvironmentViewModel EnvironmentViewModel => ShellViewModels.Instance.Environment;

        // Created on first use rather than at startup, so a session that never opens this page pays
        // nothing for the network features.
        public SettingsViewModel SettingsViewModel => ViewModels.SettingsViewModel.Instance;

        public ToolkitViewModel ToolkitViewModel => ShellViewModels.Instance.Toolkit;

        public ShellNavigationSettings NavigationSettings => ShellViewModels.Instance.NavigationSettings;

        private void OnAdvancedModeToggled(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is CheckBox box)
                NavigationSettings.SetAdvancedMode(box.IsChecked == true);
        }

        private void OnRowDensityChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RowDensityCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
                ShellViewModels.Instance.NavigationSettings is var settings &&
                Enum.TryParse<RowDensity>(tag, out var density))
            {
                settings.SetRowDensity(density);
            }
        }

        private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LanguageCombo.SelectedItem is not LanguageOption option
                || option.Code == LocalizationService.CurrentCode)
                return;

            var strings = Strings.Current;

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = strings["Settings.Language.RestartTitle"],
                Content = strings["Settings.Language.RestartBody"],
                PrimaryButtonText = strings["Settings.Language.RestartConfirm"],
                CloseButtonText = strings["Settings.Language.RestartCancel"],
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await DialogText.ShowAsync(dialog) is not ContentDialogResult.Primary)
            {
                LanguageCombo.SelectedItem = LocalizationService.Available
                    .FirstOrDefault(o => o.Code == LocalizationService.CurrentCode);
                return;
            }

            if (!LocalizationService.Choose(option.Code))
            {
                await DialogText.ShowAsync(new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = strings["Settings.Language.RestartTitle"],
                    Content = strings["Settings.Language.RestartFailed"],
                    CloseButtonText = strings["Settings.Language.RestartCancel"],
                });
            }
        }

        private void OnOpenTools(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is Button { Tag: string tag })
            {
                var page = tag switch
                {
                    "Unblock" => typeof(UnblockPage),
                    "ModSettings" => typeof(ModSettingsPage),
                    _ => typeof(ToolkitPage),
                };

                _ = Frame?.Navigate(page);
            }
        }

        private void OnOpenCrashReports(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            Frame?.Navigate(typeof(DiagnosticsPage));
        }
    }
}
