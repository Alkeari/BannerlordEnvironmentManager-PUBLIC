using BannerlordEnvironmentManager.Core.Diagnostics;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class DiagnosticFilesPage : Page
    {
        public DiagnosticFilesPage()
        {
            InitializeComponent();

            // Enter commits a limit exactly as tabbing away does, and the box marks the key handled, so
            // the repaint below has to be asked for even though nothing else will hear about the key.
            KeepCapturesBox.AddHandler(KeyDownEvent, new KeyEventHandler(OnRetentionLimitKeyDown), handledEventsToo: true);
            KeepDumpsBox.AddHandler(KeyDownEvent, new KeyEventHandler(OnRetentionLimitKeyDown), handledEventsToo: true);
        }

        public DiagnosticsViewModel DiagnosticsViewModel => ShellViewModels.Instance.Diagnostics;

        // Reading both stores on open is what keeps the lists from showing a batch another window
        // restored, or a capture that has appeared since this page was last looked at.
        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            DiagnosticsViewModel.RefreshCapturesCommand.Execute(null);

            _ = DiagnosticsViewModel.ListClearedBatchesCommand.ExecuteAsync(null);
        }

        // Right-click selects the row it landed on, so the menu and the buttons under the list always act
        // on the same batch. These two handlers on the row are first in the chain and are what guarantee
        // the menu knows which row it is about; the list-level pair below only sees a click that reached
        // it unhandled.
        private void OnClearedBatchRowContextRequested(UIElement sender, ContextRequestedEventArgs e)
        {
            _ = e;

            if ((sender as FrameworkElement)?.DataContext is ClearedBatchRowViewModel row)
                DiagnosticsViewModel.SelectedClearedBatch = row;
        }

        private void OnClearedFileRowContextRequested(UIElement sender, ContextRequestedEventArgs e)
        {
            _ = e;

            if ((sender as FrameworkElement)?.DataContext is ClearedFileRowViewModel row)
                DiagnosticsViewModel.SelectedClearedFile = row;
        }

        // A click on the empty space below the rows resolves to nothing and is left to stand, rather
        // than clearing a selection the buttons still need.
        private void OnClearedBatchContextRequested(UIElement sender, ContextRequestedEventArgs e)
        {
            _ = sender;

            if (ContextRow.Resolve<ClearedBatchRowViewModel>(e.OriginalSource) is { } row)
                DiagnosticsViewModel.SelectedClearedBatch = row;
        }

        // Same reason on the file list: the menu and Restore the selected file must mean the same row.
        private void OnClearedFileContextRequested(UIElement sender, ContextRequestedEventArgs e)
        {
            _ = sender;

            if (ContextRow.Resolve<ClearedFileRowViewModel>(e.OriginalSource) is { } row)
                DiagnosticsViewModel.SelectedClearedFile = row;
        }

        // A cleared limit is refused, and a refusal that puts back the number already in force changes
        // nothing, notifies nobody and repaints nothing: the box stays blank and the limit it is still
        // enforcing becomes invisible. Queued rather than run here, so the box has finished validating
        // and the settings have finished refusing before the figure goes back in.
        private void OnRetentionLimitLostFocus(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is NumberBox box)
                box.DispatcherQueue.TryEnqueue(() => ShowLimitInForce(box));
        }

        private void OnRetentionLimitKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key is VirtualKey.Enter && sender is NumberBox box)
                box.DispatcherQueue.TryEnqueue(() => ShowLimitInForce(box));
        }

        private void ShowLimitInForce(NumberBox box)
        {
            var limit = box == KeepDumpsBox
                ? ArtifactCaptureRetention.KeepDumps
                : ArtifactCaptureRetention.Keep;

            if (double.IsNaN(box.Value))
                box.Value = limit;

            // The NumberBox syncs its own Text with its Value only when it validates, so what the user
            // is looking at is the inner text box's text, and that is the one left blank.
            if (InputBox(box) is not { } input)
                return;

            if (ArtifactCaptureRetention.LimitTextToShow(input.Text, limit) is { } text)
            {
                input.Text = text;
                box.Text = text;
            }
        }

        private static TextBox? InputBox(DependencyObject box)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(box); i++)
            {
                var child = VisualTreeHelper.GetChild(box, i);

                if (child is TextBox found)
                    return found;

                if (InputBox(child) is { } deeper)
                    return deeper;
            }

            return null;
        }
    }
}
