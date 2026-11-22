using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class VersionsPage : Page
    {
        public string EmptyTitle => Strings.Current["Versions.Empty.Title"];

        public string EmptyReason => Strings.Current["Versions.Empty.Reason"];

        public VersionsPage()
        {
            InitializeComponent();

            ViewModel.Instances.CollectionChanged += OnInstancesChanged;
            Unloaded += (_, _) => ViewModel.Instances.CollectionChanged -= OnInstancesChanged;
            Loaded += (_, _) =>
            {
                ViewModel.Instances.CollectionChanged -= OnInstancesChanged;
                ViewModel.Instances.CollectionChanged += OnInstancesChanged;
                ApplySummary();
            };

            ApplySummary();

#if DEV_BEM
            DevRing.DeveloperSurfaces.AddTo(this);
#endif
        }

        public VersionsViewModel ViewModel => ShellViewModels.Instance.Versions;

        public ShellNavigationSettings Navigation => ShellViewModels.Instance.NavigationSettings;

        // The selected version names its own card, so the card says which version its actions act on.
        public string SelectedTitle(InstanceRowViewModel? row) =>
            row?.Name ?? Core.Localization.Strings.Current["Versions.Selected.Title"];

        public Visibility SelectedVisibility(InstanceRowViewModel? row) =>
            row is null ? Visibility.Collapsed : Visibility.Visible;

        public Visibility NothingSelectedVisibility(InstanceRowViewModel? row) =>
            row is null ? Visibility.Visible : Visibility.Collapsed;

        private void OnInstancesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            _ = sender;
            _ = e;
            ApplySummary();
        }

        // "4 versions on this machine, 48.6 GB." beside the title, as the mockup's status line.
        private void ApplySummary()
        {
            var count = ViewModel.Instances.Count;
            var bytes = ViewModel.Instances.Sum(row => row.SizeOnDiskBytes);

            Header.Status = Core.Localization.Strings.Current.Plural(
                "Versions.Summary", count, InstanceRowViewModel.FormatBytes(bytes));
        }

        // Below the compact width the two side cards fold under the table, side by side, and the table
        // keeps the larger share of the height.
        private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _ = sender;

            var compact = e.NewSize.Width < 1100 - 56;

            SideColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            SideSplitter.SetFolded(compact);
            FoldedRow.Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            TableRow.Height = compact ? new GridLength(1.2, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
            Grid.SetRow(SideCards, compact ? 2 : 1);
            Grid.SetColumn(SideCards, compact ? 0 : 1);
            PageRoot.ColumnSpacing = compact ? 0 : (double)Application.Current.Resources["AlkCardGap"];

            SideSecondColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            SideSecondRow.Height = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetRow(AddCard, compact ? 0 : 1);
            Grid.SetColumn(AddCard, compact ? 1 : 0);
            SideCards.RowSpacing = compact ? 0 : (double)Application.Current.Resources["AlkCardGap"];
        }

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
