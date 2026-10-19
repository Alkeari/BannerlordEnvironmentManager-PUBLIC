namespace BannerlordEnvironmentManager.Views
{
    using BannerlordEnvironmentManager.Core.Localization;

    public sealed partial class BootChecksPage : Page
    {
        public BootChecksPage()
        {
            InitializeComponent();
        }

        public DiagnosticsViewModel DiagnosticsViewModel => ShellViewModels.Instance.Diagnostics;

        // The boot check itself lives on Environment: it is the same load order Play holds, and
        // starting it here means putting that same load order through exactly what Launch would send.
        public EnvironmentViewModel EnvironmentViewModel => ShellViewModels.Instance.Environment;

        public ShellNavigationSettings Navigation => ShellViewModels.Instance.NavigationSettings;

        // Reading BEM's own copies costs no launch and needs no crash report to exist, so the captured
        // runs are on screen when this page opens rather than behind a button nobody knew to press.
        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            _ = DiagnosticsViewModel.ReadRunEvidenceCommand.ExecuteAsync(null);

            // The mods a search could be confined to, listed before anyone asks. An empty picker
            // behind a button nobody pressed reads as a feature that does not work.
            DiagnosticsViewModel.LoadBisectionScopeCandidatesCommand.Execute(null);
        }

        // Right-click selects the row it landed on, so the menu and the buttons under the list always
        // act on the same session. This handler is on the row itself, first in the chain, and is what
        // guarantees the menu knows which session it is about.
        private void OnBisectionSessionRowContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = e;

            if ((sender as FrameworkElement)?.DataContext is BisectionSessionRowViewModel row)
                DiagnosticsViewModel.SelectedBisectionSession = row;
        }

        // The list-level handler is the second half, for a right-click that landed on the row's own
        // padding. A click on the empty space below the rows resolves to nothing and is left to stand,
        // rather than clearing a selection the buttons still need.
        private void OnBisectionSessionContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = sender;

            if (ContextRow.Resolve<BisectionSessionRowViewModel>(e.OriginalSource) is { } row)
                DiagnosticsViewModel.SelectedBisectionSession = row;
        }

        private void OnAcceptFindingClicked(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is not Button { Tag: KnownIssueRowViewModel row } button)
                return;

            row.AcceptRisk();

            // KnownIssueRowViewModel is not observable, so the row itself does not re-bind until the
            // next check runs; this is only the same immediate confirmation the Preflight panel's own
            // Accept button gives.
            button.Content = Strings.Current["BootChecks.AcceptRiskButton.Accepted"];
            button.IsEnabled = false;
        }
    }
}
