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
            DiffAddedSegment.Text = $"Installed since the save ({SavesViewModel.Added.Count})";
            DiffRemovedSegment.Text = $"In the save, not loading now ({SavesViewModel.Removed.Count})";
            DiffVersionSegment.Text = $"At a different version ({SavesViewModel.VersionChanged.Count})";
            DiffNotRecordedSegment.Text = $"Not recorded in this save ({SavesViewModel.NotRecorded.Count})";

            if (DiffSegments.SelectedItem is null)
                DiffSegments.SelectedItem = DiffAddedSegment;
        }

        private void OnDiffSegmentSelected(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
        {
            _ = sender;
            _ = e;

            DiffAddedPanel.Visibility = ReferenceEquals(DiffSegments.SelectedItem, DiffAddedSegment) ? Visibility.Visible : Visibility.Collapsed;
            DiffRemovedPanel.Visibility = ReferenceEquals(DiffSegments.SelectedItem, DiffRemovedSegment) ? Visibility.Visible : Visibility.Collapsed;
            DiffVersionPanel.Visibility = ReferenceEquals(DiffSegments.SelectedItem, DiffVersionSegment) ? Visibility.Visible : Visibility.Collapsed;
            DiffNotRecordedPanel.Visibility = ReferenceEquals(DiffSegments.SelectedItem, DiffNotRecordedSegment) ? Visibility.Visible : Visibility.Collapsed;
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
