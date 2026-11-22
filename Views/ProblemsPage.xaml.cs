using Microsoft.UI.Xaml.Navigation;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class ProblemsPage : Page, ShellPage.IHostsViews
    {
        public ProblemsPage()
        {
            InitializeComponent();

            ViewTabs.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<Controls.SegmentedItem>
            {
                new("Findings", Core.Localization.Strings.Current["Health.View.Findings"]),
                new("Full", Core.Localization.Strings.Current["Health.View.Full"]),
            };
            ViewTabs.SelectedKey = "Findings";

            ViewModel.PropertyChanged += OnViewModelChanged;
            Unloaded += (_, _) =>
            {
                ViewModel.PropertyChanged -= OnViewModelChanged;
            };

            ApplyView();
        }

        // The page each family's full view shows, in place of navigating to it. Boot has none: its
        // experiment is Troubleshoot's Find the Culprit tab.
        private static Type? PageFor(string family) => family switch
        {
            "InstallChecks" => typeof(InstallChecksPage),
            "Overlaps" => typeof(ModOverlapsPage),
            "Safety" => typeof(ModSafetyPage),
            "Validator" => typeof(SubModuleValidatorPage),
            _ => null,
        };

        // Opened from anywhere in BEM by naming a check (PageHost): that check's chip and its full view.
        public void ShowView(string view)
        {
            if (string.IsNullOrEmpty(view))
                return;

            ViewModel.SelectedFamily = view;
            ViewTabs.SelectedKey = "Full";
            ApplyView();
        }

        private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = sender;

            if (e.PropertyName == nameof(HealthViewModel.SelectedFamily))
                DispatcherQueue.TryEnqueue(ApplyView);
        }

        // A finding's full text: what happened, what to do, and the evidence the row trims.
        private void OnFindingDetails(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is not Button { Tag: HealthFindingRowViewModel row })
                return;

            var body = new StackPanel { Spacing = 12 };

            foreach (var (text, style) in new[] { (row.Detail, "LabelText"), (row.Evidence, "MonoText") })
            {
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                body.Children.Add(new TextBlock
                {
                    Text = text,
                    Style = (Style)Application.Current.Resources[style],
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                });
            }

            var actions = new Controls.WrapPanel { Spacing = 8 };
            var actionStyle = (Style)Application.Current.Resources["RowAction"];

            foreach (var action in row.OtherActions)
            {
                var button = new Button { Content = action.Label, Command = action.Command, CommandParameter = action.Parameter, Style = actionStyle };
                ToolTipService.SetToolTip(button, action.Tooltip);
                actions.Children.Add(button);
            }

            if (row.HasSimilar)
            {
                var similar = new Button { Content = row.AcceptSimilarLabel, Command = row.AcceptSimilarCommand, Style = actionStyle };
                ToolTipService.SetToolTip(similar, Core.Localization.Strings.Current["Health.AcceptSimilar.Tooltip"]);
                actions.Children.Add(similar);

                var select = new Button { Content = row.SelectSimilarLabel, Command = row.SelectSimilarCommand, Style = actionStyle };
                ToolTipService.SetToolTip(select, Core.Localization.Strings.Current["Health.SelectSimilar.Tooltip"]);
                actions.Children.Add(select);
            }

            if (actions.Children.Count > 0)
                body.Children.Add(actions);

            ShellPage.ShowDetail(row.Headline, body);
        }

        private void OnViewTabChanged(Controls.Segmented sender, string key)
        {
            _ = sender;
            _ = key;
            ApplyView();
        }

        private void ApplyView()
        {
            var family = ViewModel.SelectedFamily;
            var page = PageFor(family);
            var findingsOnly = page is null;
            var fullOnly = family is "Validator";

            ViewTabs.Visibility = findingsOnly || fullOnly ? Visibility.Collapsed : Visibility.Visible;

            var full = fullOnly || (!findingsOnly && ViewTabs.SelectedKey == "Full");

            FindingsView.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
            FamilyFrame.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
            ShowHiddenBox.Opacity = full ? 0 : 1;
            ShowHiddenBox.IsHitTestVisible = !full;

            // Selecting findings means nothing over a check's own view, and a check that only has that
            // view has nothing left for the header, so its band goes and the check gets the height.
            SelectAllButton.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
            ClearSelectionButton.Opacity = full ? 0 : 1;
            ClearSelectionButton.IsHitTestVisible = !full;
            HealthCard.HeaderActions = fullOnly ? null : HeaderBar;

            if (full && page is not null)
                PageHost.Show(FamilyFrame, page);
        }

        public HealthViewModel ViewModel => ShellViewModels.Instance.Health;

        // The accepted list itself lives on Diagnostics: it aggregates every family - install checks,
        // boot checks, mod safety - that writes to the same store, and Health is the one place all of
        // them are already in view together.
        public DiagnosticsViewModel DiagnosticsViewModel => ShellViewModels.Instance.Diagnostics;

        public Visibility HasHidden(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is string view)
                ShowView(view);

            DiagnosticsViewModel.RefreshAcceptedFindingRows();
            EnvironmentViewModel.RefreshAcceptedRiskRows();
        }

        private string AcceptedHeading { get; } = BannerlordEnvironmentManager.Core.Localization.Strings.Current.Format("Health.AcceptedFindings");

        private void OnUnacceptAllClicked(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            UnacceptAllFlyout.Hide();
            DiagnosticsViewModel.UnacceptAllFindings();
        }

        private void OnForgetAcceptedFindingClicked(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is Button { Tag: AcceptedFindingRowViewModel row })
                row.Forget();
        }

        private string AcceptedRisksHeading { get; } = BannerlordEnvironmentManager.Core.Localization.Strings.Current["Environment.AcceptedRisksExpander.Header"];

        public EnvironmentViewModel EnvironmentViewModel => ShellViewModels.Instance.Environment;

        private void OnUnacceptAllRisksClicked(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            UnacceptAllRisksFlyout.Hide();
            EnvironmentViewModel.UnacceptAllRisks();
        }

        private void OnForgetAcceptedRiskClicked(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is Button { Tag: AcceptedRiskRowViewModel row })
                row.Forget();
        }

        private void OnRefresh(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            ViewModel.Refresh();
        }
    }
}
