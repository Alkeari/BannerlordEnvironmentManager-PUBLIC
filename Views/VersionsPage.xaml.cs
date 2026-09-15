namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class VersionsPage : Page
    {
        public VersionsPage()
        {
            InitializeComponent();

#if DEV_BEM
            DevRing.DeveloperSurfaces.AddTo(this);
#endif
        }

        public VersionsViewModel ViewModel => ShellViewModels.Instance.Versions;

        // Present while a download runs even if the status line momentarily has nothing to say, so
        // the box cannot blink out and back mid-download and move the page under it. It stays after
        // the run as well, because this is the one place a download reports how it went.
        public Visibility DownloadStatusVisibility(bool progressVisible, string? status) =>
            progressVisible || !string.IsNullOrWhiteSpace(status) ? Visibility.Visible : Visibility.Collapsed;

        // A fixed footprint while the line is churning, and as much room as the sentence needs once
        // it has stopped: double.NaN is Auto.
        public double StatusBoxHeight(bool progressVisible) => progressVisible ? 56 : double.NaN;

        // 0 is no limit.
        public int StatusBoxLines(bool progressVisible) => progressVisible ? 2 : 0;

        // A right-click has to make the row it landed on the selected one before the menu opens, or
        // every item acts on whatever was selected last and a right-click with nothing selected acts
        // on nothing. The handler on the row's own grid is the reliable half, exactly as it is for
        // Library's archive cards: ContextRequested bubbles, the list-level handler below runs only
        // if nothing between the click and the list marked the event handled, and the framework shows
        // the flyout either way.
        private void OnInstanceRowContextRequested(
            UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = e;

            if ((sender as FrameworkElement)?.DataContext is InstanceRowViewModel row)
                ViewModel.SelectedInstance = row;
        }

        // The list-level handler covers a click that reached the item container rather than the grid.
        // ContextRow walks up from whatever the pointer was over to the row that owns it; a click on
        // the empty space below the rows resolves to nothing and leaves the selection alone.
        private void OnInstanceContextRequested(
            UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = sender;

            if (ContextRow.Resolve<InstanceRowViewModel>(e.OriginalSource) is { } row)
                ViewModel.SelectedInstance = row;
        }
    }
}
