using BannerlordEnvironmentManager.Core.Install;
using BannerlordEnvironmentManager.Core.Modules;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class InstallPage : Page
    {
        public InstallPage()
        {
            InitializeComponent();
            SideSplitter.ColumnWidthChanged += FitTransientRegionToLeftColumn;

            Environment.Modules.CollectionChanged += OnModulesChanged;
            Unloaded += (_, _) => Environment.Modules.CollectionChanged -= OnModulesChanged;
            Loaded += (_, _) =>
            {
                RebuildInstalled();
                ViewModel.RefreshHistory();
                ViewModel.RefreshNotifications();
            };

        }

        public InstallViewModel ViewModel => ShellViewModels.Instance.Install;

        public ShellNavigationSettings Navigation => ShellViewModels.Instance.NavigationSettings;

        public EnvironmentViewModel Environment => ShellViewModels.Instance.Environment;

        // Basic's header button for the Notifications card, carrying how many there are.
        public string NotificationsLabel(int count) => count == 0
            ? Core.Localization.Strings.Current["Install.Card.Notifications"]
            : Core.Localization.Strings.Current.Format("Install.Simple.NotificationsDropDown.WithCount", count);

        // The BUTR stack's blocker, shown under Basic's footer while the Install card is Advanced's.
        public Visibility BasicText(bool advanced, string? text) =>
            !advanced && !string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;

        // The installed mods table: the version's community modules, narrowed by the search box.
        public System.Collections.ObjectModel.ObservableCollection<ModuleRowViewModel> InstalledRows { get; } = [];

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "x:Bind")]
        public string InstalledMeta => Core.Localization.Strings.Current.Format(
            "Install.Installed.Meta", InstalledRows.Count, Environment.Modules.Count(row => !row.IsOfficial));

        public static string StatusOf(bool outOfDate, bool probablyOutOfDate, bool unknown, bool notOnNexus, bool hasNexusId, bool workshop, bool held, bool noMatchingFile = false) =>
            Core.Localization.Strings.Current[
                held ? "Install.Status.Held"
                : outOfDate ? "Install.Status.UpdateAvailable"
                : probablyOutOfDate ? "Install.Status.ProbablyOutOfDate"
                : workshop ? "Install.Status.Workshop"
                : notOnNexus ? "Install.Status.NotOnNexus"
                : !hasNexusId && noMatchingFile ? "Install.Status.NotPublishedOnNexus"
                : !hasNexusId ? "Install.Status.NoNexusId"
                : unknown ? "Install.Status.Unknown"
                : "Install.Status.UpToDate"];

        public static Brush StatusBrush(bool outOfDate, bool probablyOutOfDate) =>
            (Brush)Application.Current.Resources[outOfDate || probablyOutOfDate ? "AlkAlertAmberBrush" : "AlkSteelGrayBrush"];

        private sealed record CategoryChoice(string Label, string Path);

        private const string UncategorizedPath = "|uncategorized|";

        private readonly List<CategoryChoice> categoryChoices = [];

        private bool rebuildingCategories;

        private string CategoryOf(ModuleRowViewModel row, IReadOnlyDictionary<string, string> filed) =>
            ModCategories.Effective(filed.TryGetValue(row.ModuleId, out var own) ? own : null, row.NexusCategory);

        private void RebuildInstalled()
        {
            var query = InstalledSearch?.Text?.Trim() ?? string.Empty;
            var filed = new ModCategoryStore(ModCategoryStore.DefaultPath(Environment.ActiveDataRoot)).Load();
            var community = Environment.Modules.Where(row => !row.IsOfficial).ToList();
            var paths = community.Select(row => CategoryOf(row, filed)).ToList();

            var wanted = (CategoryFilter?.SelectedIndex ?? 0) is > 0 and var index && index < categoryChoices.Count
                ? categoryChoices[index].Path
                : string.Empty;

            InstalledRows.Clear();

            for (var i = 0; i < community.Count; i++)
            {
                var row = community[i];

                if (wanted.Length > 0
                    && !(wanted == UncategorizedPath ? paths[i].Length == 0 : ModCategories.Matches(paths[i], wanted)))
                {
                    continue;
                }

                if (query.Length == 0
                    || row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || row.Entry.Id.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    InstalledRows.Add(row);
                }
            }

            RebuildCategoryChoices(paths, wanted);
            Bindings.Update();
        }

        // The tree is flattened with its depth shown by indent; the count beside each is its own
        // modules and everything below it. Rebuilt on every refresh so a new filing appears at once.
        private void RebuildCategoryChoices(IReadOnlyList<string> paths, string wanted)
        {
            if (CategoryFilter is null)
                return;

            var strings = Core.Localization.Strings.Current;
            var choices = new List<CategoryChoice> { new(strings["Install.Category.All"], string.Empty) };

            void Walk(IEnumerable<ModCategoryNode> nodes, int depth)
            {
                foreach (var node in nodes)
                {
                    choices.Add(new CategoryChoice(
                        new string(' ', depth * 2) + strings.Format("Install.Category.Choice", node.Name, node.Count), node.Path));
                    Walk(node.Children, depth + 1);
                }
            }

            Walk(ModCategories.BuildTree(paths), 0);

            var none = ModCategories.Uncategorized(paths);

            if (none > 0)
                choices.Add(new CategoryChoice(strings.Format("Install.Category.Choice", strings["Install.Category.None"], none), UncategorizedPath));

            categoryChoices.Clear();
            categoryChoices.AddRange(choices);

            rebuildingCategories = true;

            try
            {
                CategoryFilter.ItemsSource = choices.Select(choice => choice.Label).ToList();
                var selected = choices.FindIndex(choice => choice.Path == wanted);
                CategoryFilter.SelectedIndex = selected < 0 ? 0 : selected;
            }
            finally
            {
                rebuildingCategories = false;
            }
        }

        private void OnCategoryFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            _ = sender;
            _ = e;

            if (!rebuildingCategories)
                RebuildInstalled();
        }

        private void OnInstalledSearchChanged(object sender, TextChangedEventArgs e)
        {
            _ = sender;
            _ = e;
            RebuildInstalled();
        }

        private void OnModulesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            _ = sender;
            _ = e;
            DispatcherQueue.TryEnqueue(RebuildInstalled);
        }

        private void OnModSettingsClicked(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is Button { Tag: ModuleRowViewModel row })
                ModuleSettingsPanel.Show(row);
        }

        private async void OnUpdateModuleClicked(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is Button { Tag: ModuleRowViewModel row })
                await Environment.UpdateModuleAsync(row);
        }

        // The install destination has to be the version being played, and the only thing that pushes
        // it here is EnvironmentViewModel.Refresh. An nxm:// link opens BEM straight onto this page,
        // where Play has never run, so the destination box showed the machine install out of
        // settings.json and the mod extracted into the version nobody selected.
        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var environment = ShellViewModels.Instance.Environment;

            if (environment.Modules.Count == 0)
                await environment.Refresh();

            // A version can be downloaded, renamed or removed while Library is off screen, and the
            // chooser is the list of what an install can reach: a row for a version that is gone, or
            // no row for one just added, is a destination nobody can pick or one that cannot be hit.
            ViewModel.RefreshInstallDestinations();

            // After the chooser, not before it: what the stack button has anywhere to install to is
            // read off the ticks, so asking first answers about the list the last visit left behind.
            ViewModel.RefreshButrStackReadiness();
            ViewModel.RefreshCollectionReadiness();

            // The undo list fills itself: a list that waits for a Refresh button reads as an empty,
            // broken section, which is what he saw.
            if (ViewModel.RefreshUndoListCommand.CanExecute(null))
                ViewModel.RefreshUndoListCommand.Execute(null);
        }

        private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _ = sender;
            _ = e;

            ApplyTransientShare(TransientBars.ActualHeight);
        }

        // The bars open and close on their own, which is a size change here and nowhere else, so this
        // is where the share is divided again.
        private void OnTransientBarsSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _ = sender;

            ApplyTransientShare(e.NewSize.Height);
        }

        // The transient region is bounded as a whole, not one panel at a time. Each of the three is
        // modest alone and a guided stack walk has all three open together, which is the arrangement
        // that ruins a screen while every panel in it looks reasonable.
        //
        // The bars report work in flight and are being read while it runs, so they take the share
        // first and the results panel takes what is left and scrolls inside it. The floor is what the
        // report needs to be worth showing at all, so a run whose bars alone fill the share leaves the
        // region that much taller rather than leaving a panel with no rows in it.
        private void ApplyTransientShare(double barsHeight)
        {
            FitTransientRegionToLeftColumn();

            InstallResultsSheet.MaxHeight = Math.Max(96, Math.Max(240, ActualHeight * 0.45) - barsHeight);
        }

        // The bars and the results panel are about the installed list, so they sit over that column and
        // end where it ends. Spanning the page laid them across the side column's cards and tools.
        private void FitTransientRegionToLeftColumn()
        {
            var width = PageRows.ColumnDefinitions[0].ActualWidth;

            if (width > 0)
            {
                TransientRegion.HorizontalAlignment = HorizontalAlignment.Left;
                TransientRegion.Width = width;
            }
        }

        private void OnPageRowsSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _ = sender;
            _ = e;

            FitTransientRegionToLeftColumn();
        }

        // A download that needs a key is useless without a way to reach the field it goes in. The
        // parameter makes Settings open the Network features section and bring it into view, rather
        // than landing at the top of the page with the key field collapsed 300 lines down.
        private void OnOpenNexusKeySettings(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            _ = Frame?.Navigate(typeof(SettingsPage), "NexusKey");
        }

        // The folder pickers moved to Settings when the Library page became a page about mods rather
        // than about folders; the breadcrumb's way back is this button.
        private void OnOpenSettings(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            _ = Frame?.Navigate(typeof(SettingsPage));
        }

        // ContextRequested bubbles, and a handler on the list only runs if nothing between the click
        // and the list has already marked the event handled, while the framework shows the flyout
        // either way. The handler on the archive's own card is first in the chain, so it is what
        // guarantees the menu knows which archive it is about.
        private void OnArchiveRowContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = e;

            ViewModel.SetContextArchive((sender as FrameworkElement)?.DataContext as ModArchiveEntry);
        }

        // The list-level handler clears the archive when the click landed on empty space below the
        // last card, where every item graying out is the right answer.
        private void OnArchiveContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = sender;

            ViewModel.SetContextArchive(ResolveArchive(e.OriginalSource));
        }

        // A right-click lands on whatever is under the pointer, which inside a card can be a detected
        // module line whose DataContext is not the archive. Walking up to the first element that
        // carries one keeps those clicks targeting the archive they are part of.
        private static ModArchiveEntry? ResolveArchive(object? source)
        {
            var element = source as DependencyObject;

            while (element is not null)
            {
                if (element is ListView)
                    return null;

                if (element is FrameworkElement { DataContext: ModArchiveEntry archive })
                    return archive;

                element = VisualTreeHelper.GetParent(element);
            }

            return null;
        }

        // The restore button on an undo row. The row is the entry, so the click carries no selection.
        private void OnHistoryRollbackClick(object sender, RoutedEventArgs e)
        {
            _ = e;

            if ((sender as FrameworkElement)?.DataContext is InstallHistoryRowViewModel row && ViewModel.RollbackHistoryEntryCommand.CanExecute(row))
                ViewModel.RollbackHistoryEntryCommand.Execute(row);
        }

        private void OnUndoRestoreClick(object sender, RoutedEventArgs e)
        {
            _ = e;

            if ((sender as FrameworkElement)?.DataContext is UndoEntry entry && ViewModel.RestoreUndoEntryCommand.CanExecute(entry))
                ViewModel.RestoreUndoEntryCommand.Execute(entry);
        }

        // A right-click lands on whatever is under the pointer, which inside a row can be a TextBlock
        // with no DataContext of its own. Walking up to the first element that carries one keeps those
        // clicks targeting the row they are part of, and stops at the list so empty space below the
        // last row changes nothing.
        private static T? Resolve<T>(object? source) where T : class
        {
            var element = source as DependencyObject;

            while (element is not null)
            {
                if (element is ListView)
                    return null;

                if (element is FrameworkElement { DataContext: T item })
                    return item;

                element = VisualTreeHelper.GetParent(element);
            }

            return null;
        }

        private void OnArchiveContextMenuOpening(object sender, object e)
        {
            _ = sender;
            _ = e;

            ViewModel.RefreshArchiveContextCommands();
        }
    }
}
