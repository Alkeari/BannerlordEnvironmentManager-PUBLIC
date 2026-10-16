using Microsoft.UI.Xaml.Media;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class DiagnosticsPage : Page
    {
        public DiagnosticsPage()
        {
            InitializeComponent();

#if DEV_BEM
            DevRing.DeveloperSurfaces.AddTo(this);
#endif
        }

        public DiagnosticsViewModel DiagnosticsViewModel => ShellViewModels.Instance.Diagnostics;

        // One section is always chosen, so the picker never sits over an empty area.
        private void OnCrashEvidenceSelected(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
        {
            _ = e;

            var chosen = sender.SelectedItem?.Tag as string;

            foreach (var panel in new[] { EvidenceGame, EvidenceWindows, EvidenceDumps, EvidenceContribute })
            {
                panel.Visibility = panel.Name == chosen ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // Only the crash contribution is read from here. It is built from the crash reports this page
        // lists, so it is shown with them rather than on the Settings page where the rest of the
        // network settings live.
        public SettingsViewModel SettingsViewModel => ViewModels.SettingsViewModel.Instance;

        private void OnOpenBootChecks(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            Frame?.Navigate(typeof(BootChecksPage));
        }

        // The row records itself first, and this is the half that cannot be skipped. ContextRequested
        // bubbles, so a handler on the list runs only if nothing between the click and the list marked
        // the event handled, while the framework shows the flyout regardless: that combination is how a
        // right-click on a row opened a menu with every item grayed out and no row recorded.
        private void OnReportRowContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = e;

            DiagnosticsViewModel.SetContextReport(
                (sender as FrameworkElement)?.DataContext as CrashReportRowViewModel);
        }

        // The list-level handler stays as the second half: it is what clears the row when the click
        // landed on the empty space below the last one, where no row handler runs and every item graying
        // out is the right answer.
        private void OnReportContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = sender;

            DiagnosticsViewModel.SetContextReport(ContextRow.Resolve<CrashReportRowViewModel>(e.OriginalSource));
        }
    }

    // A right-click lands on whatever is under the pointer, which inside a row is a TextBlock whose
    // DataContext is still the row. Walking up to the first element that carries one is what keeps a
    // click on the empty space below the rows from targeting the last row. Shared because every
    // diagnostics surface with a context menu needs the same walk.
    internal static class ContextRow
    {
        public static T? Resolve<T>(object? source) where T : class
        {
            var element = source as DependencyObject;

            while (element is not null)
            {
                if (element is FrameworkElement { DataContext: T row })
                    return row;

                element = VisualTreeHelper.GetParent(element);
            }

            return null;
        }
    }
}
