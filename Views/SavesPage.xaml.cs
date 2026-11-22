using BannerlordEnvironmentManager.Core.Localization;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class SavesPage : Page
    {
        public SavesPage()
        {
            InitializeComponent();
        }

        public SavesViewModel SavesViewModel => ShellViewModels.Instance.Saves;

        public ShellNavigationSettings Navigation => ShellViewModels.Instance.NavigationSettings;

        // Below the compact width the selected save's card folds under the table.
        private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _ = sender;

            var compact = e.NewSize.Width < 1100 - 56;

            SideColumn.Width = compact ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
            SideSplitter.SetFolded(compact);
            FoldedRow.Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetRow(DiffCard, compact ? 2 : 1);
            Grid.SetColumn(DiffCard, compact ? 0 : 1);
            PageRoot.ColumnSpacing = compact ? 0 : (double)Application.Current.Resources["AlkCardGap"];
        }

        // The diff is against the load order as it stands now, so the modules have to have been read for
        // this page to say anything. Loading them here is the same call the Play tab makes on its
        // own first visit, so arriving at Saves first is not a worse starting point.
        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var environment = ShellViewModels.Instance.Environment;

            if (environment.Modules.Count == 0)
                await environment.Refresh();

            SavesViewModel.Refresh();
            RefreshSegmentCounts();
        }

        // Three of the four diff lists are empty on a healthy setup, so the four columns became one
        // selected list. The counts live on the segments so the summary still reads at a glance.
        private void RefreshSegmentCounts()
        {
            DiffAddedSegment.Content = Strings.Current.Format("Saves.SegmentCount", Strings.Current["Saves.AddedSegment"], SavesViewModel.Added.Count);
            DiffRemovedSegment.Content = Strings.Current.Format("Saves.SegmentCount", Strings.Current["Saves.RemovedSegment"], SavesViewModel.Removed.Count);
            DiffVersionSegment.Content = Strings.Current.Format("Saves.SegmentCount", Strings.Current["Saves.VersionChangedSegment"], SavesViewModel.VersionChanged.Count);
            DiffNotRecordedSegment.Content = Strings.Current.Format("Saves.SegmentCount", Strings.Current["Saves.NotRecordedSegment"], SavesViewModel.NotRecorded.Count);
            ShowDiffSegment();
        }

        // One chip is on at a time, and pressing the one already on keeps it on.
        private void OnDiffSegmentChosen(object sender, RoutedEventArgs e)
        {
            _ = e;

            foreach (var chip in DiffSegments.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>())
                chip.IsChecked = ReferenceEquals(chip, sender);

            ShowDiffSegment();
        }

        private void ShowDiffSegment()
        {
            DiffAddedPanel.Visibility = DiffAddedSegment.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            DiffRemovedPanel.Visibility = DiffRemovedSegment.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            DiffVersionPanel.Visibility = DiffVersionSegment.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            DiffNotRecordedPanel.Visibility = DiffNotRecordedSegment.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnSaveRowContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = e;

            SavesViewModel.SetContextSave((sender as FrameworkElement)?.DataContext as SaveRowViewModel);
        }

        // The second half: it is what clears the row when the click landed on the empty space below the
        // last one, where no row handler runs and every item graying out is the right answer.
        private void OnSaveContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = sender;

            SavesViewModel.SetContextSave(Resolve(e.OriginalSource));
        }

        private static SaveRowViewModel? Resolve(object? source)
        {
            var element = source as DependencyObject;

            while (element is not null)
            {
                if (element is ListView)
                    return null;

                if (element is FrameworkElement { DataContext: SaveRowViewModel row })
                    return row;

                element = VisualTreeHelper.GetParent(element);
            }

            return null;
        }
    }
}
