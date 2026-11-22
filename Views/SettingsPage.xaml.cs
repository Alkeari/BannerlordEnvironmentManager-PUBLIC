namespace BannerlordEnvironmentManager.Views
{
    using BannerlordEnvironmentManager.Core.Localization;
    using BannerlordEnvironmentManager.Localization;
    using BannerlordEnvironmentManager.Services;
    using BannerlordEnvironmentManager.ViewModels;
    using Microsoft.UI.Xaml.Navigation;

    public sealed partial class SettingsPage : Page, ShellPage.IHostsViews
    {
        public SettingsPage()
        {
            InitializeComponent();

            Header.Status = Strings.Current["Settings.Status"];

            CompactDensityItem.Content = Strings.Current["Settings.ModuleRowDensityCombo.CompactDefault"];
            StandardDensityItem.Content = Strings.Current["Settings.ModuleRowDensityCombo.Standard"];
            ComfortableDensityItem.Content = Strings.Current["Settings.ModuleRowDensityCombo.Comfortable"];

            BuildGroups();

            NavigationSettings.PropertyChanged += OnNavigationSettingsChanged;
            Unloaded += (_, _) => NavigationSettings.PropertyChanged -= OnNavigationSettingsChanged;

#if DEV_BEM
            DevRing.DeveloperSurfaces.AddTo(this);
#endif
        }

        public string AppVersionText => $"Version {Services.AppVersion.Display} by Alkeari";

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            SettingsViewModel.RefreshExceptionManager();
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
            if (e.Parameter is string view)
                ShowView(view);
        }

        // The groups, in the order the mockup lists them. Load Order is a power-user group and is
        // listed only in Advanced Mode, the same thing its section did before.
        private readonly System.Collections.ObjectModel.ObservableCollection<Controls.SegmentedItem> groups = [];

        private void BuildGroups()
        {
            var strings = Strings.Current;
            var advanced = NavigationSettings.IsAdvancedMode;

            groups.Clear();
            groups.Add(new("Folders", strings["Settings.FoldersSection"], strings["Settings.FoldersSection.Blurb"]));
            // Launch and Diagnostics are Advanced, like the other managers keep them: the defaults
            // launch the game, and the version and logs matter only when something is being chased.
            if (advanced)
                groups.Add(new("Launch", strings["Settings.LaunchSection"], strings["Settings.SharedGameSettingsSection"]));

            groups.Add(new("Display", strings["Settings.Group.Display"], strings["Settings.DisplaySection"]));

            if (advanced)
                groups.Add(new("LoadOrder", strings["Settings.LoadOrderSection"], strings["Settings.LoadOrderSection"]));

            groups.Add(new("Network", strings["Settings.Group.Network"], strings["Settings.NetworkFeaturesExpander.Blurb"]));
            if (advanced)
                groups.Add(new("Diagnostics", strings["Settings.DiagnosticsSection"], strings["Settings.DiagnosticsSection.Blurb"]));

            Groups.ItemsSource = groups;

            if (groups.All(group => group.Key != Groups.SelectedKey))
                Groups.SelectedKey = groups[0].Key;

            ShowGroup(Groups.SelectedKey);
        }

        // Opened from anywhere by naming a section.
        public void ShowView(string view)
        {
            if (view == "NexusKey")
            {
                Groups.SelectedKey = "Network";
                NexusKeySection.StartBringIntoView();
            }
        }

        private void OnNavigationSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = sender;

            if (e.PropertyName == nameof(ShellNavigationSettings.IsAdvancedMode))
                DispatcherQueue.TryEnqueue(BuildGroups);
        }

        private void OnGroupChanged(Controls.Segmented sender, string key)
        {
            _ = sender;
            ShowGroup(key);
        }

        private void ShowGroup(string key)
        {
            FoldersCard.Visibility = Show(key == "Folders");
            LaunchCard.Visibility = Show(key == "Launch");
            DisplayCard.Visibility = Show(key == "Display");
            LoadOrderCard.Visibility = Show(key == "LoadOrder");
            NetworkCard.Visibility = Show(key == "Network");
            DiagnosticsCard.Visibility = Show(key == "Diagnostics");

            static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        }

        // Below the compact width the group list folds into a chip row above the card, so the card
        // keeps the whole width.
        private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _ = sender;

            var compact = e.NewSize.Width < 1100 - 56;
            var mode = compact ? Controls.SegmentedMode.Chips : Controls.SegmentedMode.List;

            if (Groups.Mode == mode && e.PreviousSize.Width > 0)
                return;

            Groups.Mode = mode;
            Grid.SetRow(Groups, compact ? 1 : 2);
            Grid.SetColumnSpan(Groups, compact ? 2 : 1);
            Groups.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 24, 0);
            Grid.SetColumn(CardHost, compact ? 0 : 1);
            Grid.SetColumnSpan(CardHost, compact ? 2 : 1);
            GroupColumn.Width = compact ? new GridLength(0) : new GridLength(180);
            GroupSplitter.SetFolded(compact);
        }

        public InstallViewModel ViewModel => ShellViewModels.Instance.Install;

        public EnvironmentViewModel EnvironmentViewModel => ShellViewModels.Instance.Environment;

        public DiagnosticsViewModel DiagnosticsViewModel => ShellViewModels.Instance.Diagnostics;

        // Created on first use rather than at startup, so a session that never opens this page pays
        // nothing for the network features.
        public SettingsViewModel SettingsViewModel => ViewModels.SettingsViewModel.Instance;

        public ToolkitViewModel ToolkitViewModel => ShellViewModels.Instance.Toolkit;

        public ShellNavigationSettings NavigationSettings => ShellViewModels.Instance.NavigationSettings;

        private void OnRunWalkthrough(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            NavigationSettings.RequestWalkthrough();
        }

        private void OnAdvancedModeToggled(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton toggle)
                NavigationSettings.SetAdvancedMode(toggle.IsChecked == true);
        }

        // Every splitter and adjustable section reads this store when its page loads, so emptying it
        // returns them all to the size they were built with on the next visit.
        private void OnResetLayoutClicked(object sender, RoutedEventArgs e)
        {
            var reset = new Core.Settings.SectionLayoutStore().Reset();
            ResetLayoutStatus.Text = reset ? Strings.Current["Settings.ResetLayout.Done"] : string.Empty;
            ResetLayoutStatus.Visibility = reset ? Visibility.Visible : Visibility.Collapsed;
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

        private void OnOpenCrashReports(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            Frame?.Navigate(typeof(DiagnosticsPage));
        }
    }
}
