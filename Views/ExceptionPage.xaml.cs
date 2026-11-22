namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class ExceptionPage : Page
    {
        public ExceptionPage()
        {
            InitializeComponent();
        }

        public ExceptionWindowViewModel ViewModel { get; private set; } = new(string.Empty);

        protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e?.Parameter is ExceptionWindowViewModel viewModel)
            {
                ViewModel = viewModel;
                Bindings.Update();
            }

            await ViewModel.LoadAsync();
        }

        // Eight panels and one of them showing. The pressed tab takes the primary style, which is how
        // every other choice of one in the app says which is current.
        private void OnTab(object sender, RoutedEventArgs e)
        {
            if (sender is not Button pressed || pressed.Tag is not string tag || !int.TryParse(tag, out var chosen))
                return;

            UIElement[] panels = [PanelException, PanelSuspects, PanelMods, PanelAssemblies, PanelPatches, PanelLog, PanelScreenshot, PanelModules];
            Button[] tabs = [TabException, TabSuspects, TabMods, TabAssemblies, TabPatches, TabLog, TabScreenshot, TabModules];

            for (var i = 0; i < panels.Length; i++)
            {
                panels[i].Visibility = i == chosen ? Visibility.Visible : Visibility.Collapsed;
                tabs[i].Style = (Style)Application.Current.Resources[i == chosen ? "PrimaryAction" : "Action"];
            }

            // The log ends at the failure, so it opens at its end; every other panel opens at its top.
            PanelScroll.UpdateLayout();
            _ = PanelScroll.ChangeView(null, panels[chosen] == PanelLog ? PanelScroll.ScrollableHeight : 0, null, true);
        }
    }
}
