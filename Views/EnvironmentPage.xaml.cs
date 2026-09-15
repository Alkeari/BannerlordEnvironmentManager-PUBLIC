using System.Collections.Specialized;
using BannerlordEnvironmentManager.Core.Launcher;
using BannerlordEnvironmentManager.Core.LoadOrder;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class EnvironmentPage : Page
    {
        public EnvironmentPage()
        {
            InitializeComponent();

#if DEV_BEM
            DevRing.DeveloperSurfaces.AddTo(this);
#endif

            // The view model is a singleton and this handler is never detached, so the page must be
            // cached: a fresh instance per navigation would stack up another subscription each time.
            NavigationCacheMode = NavigationCacheMode.Required;
            ViewModel.DisplayRows.CollectionChanged += OnDisplayRowsChanged;

            // Both are registered for handled events too: a ListViewItem marks a press handled for its
            // own selection visuals, and the list's own type-ahead can mark a key handled, either of
            // which would leave a plain XAML handler on the list never running.
            ModuleList.AddHandler(
                PointerPressedEvent,
                new Microsoft.UI.Xaml.Input.PointerEventHandler(OnModuleListPointerPressed),
                handledEventsToo: true);

            // The release half of the empty-space click. Registered for handled events too for the same
            // reason the press is: a ListViewItem marks both for its own visuals.
            ModuleList.AddHandler(
                PointerReleasedEvent,
                new Microsoft.UI.Xaml.Input.PointerEventHandler(OnModuleListPointerReleased),
                handledEventsToo: true);

            ModuleList.AddHandler(
                KeyDownEvent,
                new Microsoft.UI.Xaml.Input.KeyEventHandler(OnModuleListKeyDown),
                handledEventsToo: true);

            // The page is the only place a dialog can be shown from. Without it the view model still
            // runs the preflight and still shows what it found; it simply launches without asking.
            ViewModel.AskAboutPreflight = AskAboutPreflightAsync;
        }

        public EnvironmentViewModel ViewModel => ShellViewModels.Instance.Environment;

        public ShellNavigationSettings Navigation => ShellViewModels.Instance.NavigationSettings;

        // Amber only when the order is close to the limit or past it, so the line reads as a plain
        // fact most of the time and as a warning exactly when it is one.
        public Brush CommandLineBrush(bool tight) =>
            (Brush)Application.Current.Resources[tight ? "AlkAlertAmberBrush" : "AlkSteelGrayBrush"];

        // A download that is running, and nothing else. Play shows the progress of a download
        // somebody started on Versions because the user is very likely sitting here while it runs,
        // but what a download had to say when it ended belongs on the page the button that started
        // it lives on, said once: the same failure used to be printed here and twice over there.
        public Visibility DownloadStatusVisibility(bool progressVisible) =>
            progressVisible ? Visibility.Visible : Visibility.Collapsed;

        // EmptyState's Title and Reason are plain strings on a UserControl, so they are resolved here
        // rather than through Loc's attached properties, which only reach TextBlocks and
        // ContentControls.
        public string NoVersionsTitle => Strings.Current["Environment.NoVersions.Title"];

        public string NoVersionsReason => Strings.Current["Environment.NoVersions.Reason"];

        public Visibility NoVersionsVisibility(bool hasInstalledVersions) =>
            hasInstalledVersions ? Visibility.Collapsed : Visibility.Visible;

        private void OnOpenVersionsRequested(object sender, RoutedEventArgs e) =>
            Frame?.Navigate(typeof(VersionsPage));

        // The load order's floor: six rows at the standard density plus the panel's own frame. Six is
        // where a row being dragged still has visible neighbors above and below it to be dropped
        // between, which is most of what the list is read for. Everything that appears later is handed
        // what is left after this comes off the top.
        private const double LoadOrderFloorHeight = 320;

        // The root grid's padding plus the spacing between its seven rows. Neither can be read off a
        // band, and both come out of the same height everything else is competing for.
        private const double PageFrameHeight = 102;

        // What the issue panel costs before a single issue is in it: the hairline, the padding and the
        // count row with Fix All on it.
        private const double IssuePanelChromeHeight = 64;

        // One collapsed disclosure header and the spacing above it. Three of these are on the page
        // whenever all three have something to say, open or not.
        private const double DisclosureHeaderHeight = 52;

        // A region capped below this is a control that opens onto nothing, which is worse than a short
        // load order. This is the one case where the floor yields, it is bounded by these two numbers,
        // and closing whatever was opened gives the height straight back.
        private const double MinimumRegionHeight = 96;

        private const double MaximumIssueListHeight = 180;

        private const double MaximumDisclosureHeight = 300;

        private Expander? _openDisclosure;

        private Expander[] Disclosures =>
            [PreflightDisclosure, AcceptedRisksDisclosure, SortExplanationDisclosure];

        private void OnLayoutBandSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _ = sender;
            _ = e;
            UpdateRegionBudget();
        }

        // One at a time. Each disclosure carried a cap of its own and each cap was defensible, but
        // nothing stopped all three being open together, and three of them plus the issue panel came to
        // more than the window had left once the bands above had taken theirs. Closing the others is
        // also what makes the one just opened the thing on screen rather than the thing below two
        // others.
        private void OnDisclosureExpanding(Expander sender, ExpanderExpandingEventArgs args)
        {
            _ = args;

            foreach (var other in Disclosures)
            {
                if (!ReferenceEquals(other, sender))
                    other.IsExpanded = false;
            }

            _openDisclosure = sender;
            UpdateRegionBudget();
        }

        private void OnDisclosureCollapsed(Expander sender, ExpanderCollapsedEventArgs args)
        {
            _ = args;

            if (ReferenceEquals(_openDisclosure, sender))
                _openDisclosure = null;

            UpdateRegionBudget();
        }

        // The issue panel and the open disclosure are the two regions that appear and grow after the
        // page is up, so they are handed a share of what is genuinely spare instead of each carrying a
        // fixed cap. Spare is measured, not assumed: the bands above and below are asked how tall they
        // actually are, the load order's floor comes off the top, and what remains is split. Nothing in
        // here reads the height of a region it sets, so setting one cannot move the number that sized it.
        private void UpdateRegionBudget()
        {
            var available = PageRoot.ActualHeight;
            if (available <= 0)
                return;

            var issuesLive = IssuePanel.Visibility == Visibility.Visible;
            var disclosureOpen = _openDisclosure is not null;

            var bands = HeaderBand.ActualHeight + ToolbarBand.ActualHeight
                + NoticeBand.ActualHeight + StatusBand.ActualHeight;

            var chrome = (issuesLive ? IssuePanelChromeHeight : 0)
                + (Disclosures.Count(d => d.Visibility == Visibility.Visible) * DisclosureHeaderHeight);

            var spare = Math.Max(0, available - PageFrameHeight - bands - chrome - LoadOrderFloorHeight);

            // Whichever of the two is live takes the spare; both live, the issues take the smaller
            // share, because an issue row is one line and a disclosure holds a table.
            var issueShare = issuesLive ? (disclosureOpen ? 0.4 : 1.0) : 0;
            var disclosureShare = disclosureOpen ? (issuesLive ? 0.6 : 1.0) : 0;

            IssueList.MaxHeight = Math.Clamp(
                spare * issueShare, MinimumRegionHeight, MaximumIssueListHeight);

            var disclosureHeight = Math.Clamp(
                spare * disclosureShare, MinimumRegionHeight, MaximumDisclosureHeight);

            PreflightScroller.MaxHeight = disclosureHeight;
            AcceptedRisksScroller.MaxHeight = disclosureHeight;
            SortExplanationScroller.MaxHeight = disclosureHeight;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var install = ShellViewModels.Instance.Install;

            if (!string.IsNullOrWhiteSpace(install.GameInstallPath))
                ViewModel.GameInstallPath = install.GameInstallPath;

            // The watch can be armed or stopped from the Watch page, and it outlives this window, so
            // what the Launch button says about it, and whether anything of BEM's own is left in the
            // game, are both re-read every time the tab is opened. Neither depends on the module scan
            // below, so both stay ahead of it rather than waiting behind a refresh that can run seconds.
            ViewModel.RefreshCompanionState();

            // Versions are re-read on every visit for the same reason: one can arrive from outside
            // BEM entirely, either as a download the Steam client staged or as an instance folder the
            // owner moved. Fire-and-forget because it fetches Steam's branch list.
            _ = ShellViewModels.Instance.VersionSwitcher.RefreshIfIdleAsync();

            if (string.IsNullOrWhiteSpace(install.GameInstallPath)
                && !string.IsNullOrWhiteSpace(ViewModel.GameInstallPath))
            {
                install.GameInstallPath = ViewModel.GameInstallPath;
            }

            // Awaited rather than fired-and-forgotten: Refresh only catches the disk-read exceptions
            // it knows how to explain, so anything else used to vanish as an unobserved task exception
            // with the module list left empty and nothing in the log to say why. Awaiting it here lets
            // an unexpected failure reach the app's own unhandled-exception dialog instead of hiding.
            if (ViewModel.Modules.Count == 0)
                await ViewModel.Refresh();
        }

        private async void OnRefreshClicked(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            // Every change is already on disk, so refreshing discards nothing. The one exception is a
            // change held back because a launcher is running, which a reload would throw away.
            if (ViewModel.HasHeldChanges && !await ConfirmDiscardAsync())
                return;

            ViewModel.HasHeldChanges = false;
            await ViewModel.Refresh();
            await ShellViewModels.Instance.VersionSwitcher.RefreshIfIdleAsync();
        }

        // Same WinUI limitation OnVisibleModulesChanged always worked around (microsoft-ui-xaml #9685: a
        // drag raises Remove then Add, never Move), now over a heterogeneous list. A module row's new
        // neighbor tells Modules its new order; a divider row's new neighbor tells it its new anchor.
        // Both are read from the same post-drag DisplayRows snapshot before anything is written back.
        //
        // Only the Add half is acted on. By the time WinUI raises it, the Remove half has already run,
        // so DisplayRows already holds the complete, settled post-drop order - reacting to Remove too
        // would briefly see the dragged row missing entirely, write that (a real divider deleted, then
        // restored a moment later by the Add) to dividers.json, and double every disk write and pin
        // recompute this handler does per drop.
        private void OnDisplayRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            _ = sender;

            LogDrag($"DisplayRowsChanged action={e.Action} oldIndex={e.OldStartingIndex} newIndex={e.NewStartingIndex}");

            if (e.Action != NotifyCollectionChangedAction.Add)
                return;

            // Only a drop is a reorder. The view model replaces this collection wholesale whenever
            // the module list or the sections change, and reading anchors back out of a list that is
            // mid-rebuild would write a view of the world that was never on screen.
            if (ViewModel.IsRebuildingDisplayRows)
                return;

            var rows = ViewModel.DisplayRows;
            var moduleRows = rows.OfType<ModuleRowViewModel>().ToList();

            if (moduleRows.Count != ViewModel.Modules.Count)
                return;

            var reorderedDividers = new List<LoadOrderDivider>(ViewModel.Dividers.Count);

            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i] is not DividerRowViewModel divider)
                    continue;

                ModuleId? anchor = null;

                for (var j = i + 1; j < rows.Count; j++)
                {
                    if (rows[j] is ModuleRowViewModel module)
                    {
                        anchor = module.Entry.Id;
                        break;
                    }
                }

                reorderedDividers.Add(divider.Divider with { AnchorId = anchor });
            }

            var moduleOrderChanged = !moduleRows.SequenceEqual(ViewModel.Modules);
            var dividersChanged = !reorderedDividers.SequenceEqual(ViewModel.Dividers);

            if (!moduleOrderChanged && !dividersChanged)
                return;

            if (moduleOrderChanged)
            {
                ViewModel.Modules.Clear();

                foreach (var row in moduleRows)
                    ViewModel.Modules.Add(row);
            }

            ViewModel.Dividers.Clear();

            foreach (var divider in reorderedDividers)
                ViewModel.Dividers.Add(divider);

            ViewModel.SaveDividersPublic();

            // Same reasoning as before: we are inside DisplayRows' own CollectionChanged, and a refused
            // drop rebuilds it to put the row back, which cannot happen while this event is still raising.
            if (moduleOrderChanged)
                DispatcherQueue.TryEnqueue(ViewModel.OnOrderChangedByUser);
        }

        // Only fire when CanDragItems is true, which the ListView now is (see the XAML). Timestamped
        // entries around a real drag against DisplayRowsChanged's own log line are the one way to tell,
        // as fact rather than guesswork, whether anything besides WinUI's own reorder runs mid-gesture -
        // temporary aid for diagnosing the reported drag jumpiness; safe to remove once that is settled.
        private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            _ = sender;
            _ = e;

            LogDrag("DragItemsStarting");
        }

        private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e)
        {
            _ = sender;

            LogDrag($"DragItemsCompleted dropResult={e.DropResult}");
        }

        private static void LogDrag(string message)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bem_perf.log"),
                    $"[D] {DateTime.Now:HH:mm:ss.fff} {message}\r\n");
            }
            catch
            {
            }
        }

        // The row is recorded twice on purpose, and this one is the reliable half. ContextRequested
        // bubbles, and a handler on the list only runs if nothing between the click and the list has
        // already marked the event handled; the flyout is shown by the framework either way, so a
        // list-only handler can leave every item grayed out with no row recorded at all. A handler on
        // the row itself is the first one in the chain and cannot be skipped.
        // Multi-select lives on the rows, not on the ListView. Native multi-select draws a checkbox per
        // row that reads as the module being enabled, so the list stays on Single selection (no
        // checkboxes at all) and Ctrl+click toggles a row's highlight in and out of the set the context
        // menu acts on.
        private void OnModuleSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _ = sender;
            _ = e;

            // The view-to-VM half of what used to be a TwoWay x:Bind on SelectedItem (see the XAML
            // comment above the ListView). A safe cast leaves SelectedModule null for a divider row,
            // which OnSelectedModuleChanged/ShowDetails already treat as nothing selected (the same
            // state CloseDetails puts it in), instead of the hard cast x:Bind generated, which threw.
            ViewModel.SelectedModule = ModuleList.SelectedItem as ModuleRowViewModel;

            if (ModuleList.SelectedItem is not ModuleRowViewModel row)
                return;

            // A right-click belongs to the context menu, and SetContextSelection alone says what one
            // does to the selection. Shift means a range on a left click and the extended menu on a
            // right one, so the list moving its own selection underneath the second must never be
            // read as the first. Consumed here, and again as the flyout opens, so it cannot outlive
            // the click that set it.
            if (contextClickThisPress)
            {
                contextClickThisPress = false;
                return;
            }

            if (IsDown(Windows.System.VirtualKey.Control) || IsDown(Windows.System.VirtualKey.Shift))
            {
                // A modified click is settled in OnModuleListPointerPressed, where the row under the
                // pointer and the row the list is about to select are still two different facts. The
                // two handlers can arrive in either order, so the set is written once, by whichever of
                // them sees the click first, and the other leaves it alone.
                if (!gestureHandledThisClick)
                    ApplyGesture(row);

                return;
            }

            // A plain click, or an arrow key: the selection collapses to this row and stands the
            // multi-set down, so a stale selection never spills into a later right-click.
            ViewModel.ApplySelectionGesture(ListSelectionGesture.Replace, row);
        }

        // The row a modified click landed on has already been dealt with, so the list moving its own
        // selection during that same click must not decide the set a second time.
        private bool gestureHandledThisClick;

        // Set by the press that opens a context menu and cleared by whichever of the two things that
        // can follow it happens first.
        private bool contextClickThisPress;

        // handledEventsToo, because a ListViewItem marks the press handled for its own selection
        // visuals before it reaches the list.
        private void OnModuleListPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _ = sender;

            gestureHandledThisClick = false;
            emptySpacePressed = false;

            var properties = e.GetCurrentPoint(ModuleList).Properties;

            if (properties.IsMiddleButtonPressed)
                return;

            // A right-click is the context menu's, and SetContextSelection already says what one
            // outside the selection does to it. It is recorded so a Shift+right-click, which is a
            // request for the extended menu, cannot also be taken for a range-select.
            if (properties.IsRightButtonPressed)
            {
                contextClickThisPress = true;
                return;
            }

            if (!IsDown(Windows.System.VirtualKey.Control) && !IsDown(Windows.System.VirtualKey.Shift))
            {
                // A plain left-click that landed on nothing. Explorer deselects everything on one, and
                // a modified click does not, so the two are told apart here while the keys are held.
                emptySpacePressed = LandedOnNothing(e.OriginalSource);
                return;
            }

            if (ResolveModuleRow(e.OriginalSource) is not { } row)
                return;

            ApplyGesture(row);
            gestureHandledThisClick = true;
        }

        // Set by a plain left press on empty space and consumed by the release that follows it.
        private bool emptySpacePressed;

        // Cleared on the release rather than on the press. BEM has no marquee, so a press-and-drag from
        // empty space means nothing, and acting on the press would drop the selection under a stray
        // drag or under a touch pan that started below the last row. A pan cancels the pointer instead
        // of releasing it, so it never reaches here, and the next press resets the flag either way.
        private void OnModuleListPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _ = sender;

            if (!emptySpacePressed)
                return;

            emptySpacePressed = false;

            // Only the left button, and only when the release lands on nothing as well: a drag that
            // ended over a row is not a click on empty space.
            if (e.GetCurrentPoint(ModuleList).Properties.PointerUpdateKind
                    is not Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased
                || !LandedOnNothing(e.OriginalSource))
                return;

            ViewModel.ApplySelectionGesture(ListSelectionGesture.Clear, null);

            // The multi-set and the row the list is drawing as its own selection are two different
            // things, and clicking nothing means neither one survives. Written to the list rather than
            // to SelectedModule, which is already null while a divider row is the selected one and so
            // would raise nothing for the OneWay binding to carry back.
            ModuleList.SelectedItem = null;
        }

        // Empty space is decided against the live visual tree under the pointer, not against item
        // bounds or an index: the press either landed inside a realized row container or it did not.
        // That holds when the list is scrolled, when virtualization has thrown the offscreen containers
        // away, and when the list is shorter than its viewport. A divider row sits in a container like
        // every other row, so clicking one is not clicking nothing. The scroll bar is the one piece of
        // the list's own chrome a pointer can land on, and dragging its thumb is not a click on the
        // background either.
        private static bool LandedOnNothing(object? source)
        {
            var element = source as DependencyObject;

            while (element is not null)
            {
                if (element is ListView)
                    return true;

                if (element is Microsoft.UI.Xaml.Controls.Primitives.SelectorItem
                    or Microsoft.UI.Xaml.Controls.Primitives.ScrollBar)
                    return false;

                element = VisualTreeHelper.GetParent(element);
            }

            return true;
        }

        private void ApplyGesture(ModuleRowViewModel row)
        {
            var ctrl = IsDown(Windows.System.VirtualKey.Control);
            var shift = IsDown(Windows.System.VirtualKey.Shift);

            ViewModel.ApplySelectionGesture(
                shift
                    ? ctrl ? ListSelectionGesture.ExtendAdd : ListSelectionGesture.Extend
                    : ListSelectionGesture.Toggle,
                row);
        }

        // Ctrl+A on the list only. The search box is not inside it, so a select-all typed in a text
        // field is never taken for one over the modules; a text field that does sit inside a row is
        // checked for by hand, since the handler is registered for handled events too.
        private void OnModuleListKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            _ = sender;

            if (e.Key is not Windows.System.VirtualKey.A
                || !IsDown(Windows.System.VirtualKey.Control)
                || IsDown(Windows.System.VirtualKey.Shift)
                || IsDown(Windows.System.VirtualKey.Menu))
                return;

            if (XamlRoot is { } root
                && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is TextBox or RichEditBox or AutoSuggestBox)
                return;

            if (!ViewModel.SelectAllShownCommand.CanExecute(null))
                return;

            // The Bulk menu's Select All under another name: one command, one scope, so the two can
            // never disagree about what all of them means.
            ViewModel.SelectAllShownCommand.Execute(null);
            e.Handled = true;
        }

        private static bool IsDown(Windows.System.VirtualKey key) =>
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
             & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        private void OnModuleRowContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = e;

            contextMenuExtended = IsDown(Windows.System.VirtualKey.Shift);

            ViewModel.SetContextModule((sender as FrameworkElement)?.DataContext as ModuleRowViewModel);
        }

        // Windows Explorer's extended menu, on the one list where the menu was long enough to need
        // it. ContextRequestedEventArgs carries no modifier state and MenuFlyout has no notion of an
        // extended menu, so the answer is read the same way every selection gesture on this page
        // reads one: InputKeyboardSource polls the thread's own key state, and this runs inside the
        // context request rather than after it, while the key is still held.
        private bool contextMenuExtended;

        // The list-level handler stays as the second half: it is what clears the row when the click
        // landed on empty space below the last one, where no row handler runs and every item graying
        // out is the correct answer.
        private void OnModuleContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = sender;

            contextMenuExtended = IsDown(Windows.System.VirtualKey.Shift);

            ViewModel.SetContextModule(ResolveModuleRow(e.OriginalSource));
        }

        // A right-click lands on whatever is under the pointer, which inside a row can be a note in
        // the expander, whose DataContext is a string and not the row. Walking up to the first
        // element that carries a row is what keeps those clicks targeting the module they are part of.
        private static ModuleRowViewModel? ResolveModuleRow(object? source)
        {
            var element = source as DependencyObject;

            while (element is not null)
            {
                if (element is ListView)
                    return null;

                if (element is FrameworkElement { DataContext: ModuleRowViewModel row })
                    return row;

                element = VisualTreeHelper.GetParent(element);
            }

            return null;
        }

        // Whichever of the two handlers ran, and whenever it ran relative to the flyout being shown,
        // the items are re-evaluated here against the row that is recorded at the moment they appear.
        private void OnModuleContextMenuOpening(object sender, object e)
        {
            _ = e;

            // Whichever row was recorded when the right-click landed, the multi command acts on the rows
            // the user highlighted with Ctrl+click, so the set is read here as the menu appears.
            ViewModel.SetContextSelection(
                ViewModel.ContextModule,
                ViewModel.VisibleModules.Where(module => module.IsUiSelected));
            ViewModel.RefreshContextCommands();

            if (sender is MenuFlyout flyout)
                RebuildModuleContextMenu(flyout);
        }

        // The batch items are built here rather than declared in the XAML because MenuFlyout has no
        // ItemsSource, and because which of the thirteen appear at all depends on the selection: an
        // action no selected module can take is not offered. While a multi-selection stands, the
        // single-module items are hidden rather than removed, so the menu only ever offers what it can
        // do to the whole set.
        private readonly List<MenuFlyoutItemBase> batchMenuItems = [];

#if DEV_BEM
        // Open SubModule.xml is built by the outer ring (DevRing\DeveloperSurfaces.cs) rather than
        // declared in the XAML, and the map below needs it by name so it hides and shows with the rest
        // of the single-module items. The ring fills this in from the constructor, before any menu
        // opens, so it is never read null.
        internal MenuFlyoutItem ModuleOpenManifestItem = null!;
#endif

        // Every declared item against the name Core decides by. The XAML declares them in this same
        // order, so nothing here reorders anything: it only says what is shown.
        private (ModuleMenuItem Entry, MenuFlyoutItemBase Item)[] ModuleMenuMap() =>
        [
            (ModuleMenuItem.Toggle, ModuleToggleItem),
            (ModuleMenuItem.EnableWithDependencies, ModuleEnableWithDependenciesItem),
            (ModuleMenuItem.DisableWithDependents, ModuleDisableWithDependentsItem),
            (ModuleMenuItem.Uninstall, ModuleUninstallItem),
            (ModuleMenuItem.Prune, ModulePruneItem),
            (ModuleMenuItem.MoveToTop, ModuleMoveToTopItem),
            (ModuleMenuItem.MoveToBottom, ModuleMoveToBottomItem),
            (ModuleMenuItem.MoveUp, ModuleMoveUpItem),
            (ModuleMenuItem.MoveDown, ModuleMoveDownItem),
            (ModuleMenuItem.Update, ModuleUpdateItem),
            (ModuleMenuItem.OpenModPage, ModuleOpenModPageItem),
            (ModuleMenuItem.TogglePin, ModulePinItem),
            (ModuleMenuItem.OpenFolder, ModuleOpenFolderItem),
#if DEV_BEM
            (ModuleMenuItem.OpenManifest, ModuleOpenManifestItem),
#endif
            (ModuleMenuItem.CopyId, ModuleCopyIdItem),
            (ModuleMenuItem.InsertDividerAbove, ModuleInsertDividerAboveItem),
            (ModuleMenuItem.InsertDividerBelow, ModuleInsertDividerBelowItem),
            (ModuleMenuItem.SetNexusModId, ModuleSetNexusIdItem),
            (ModuleMenuItem.ForgetModId, ModuleForgetModIdItem),
            (ModuleMenuItem.NotOnNexus, NotOnNexusMenuItem),
            (ModuleMenuItem.Deselect, ModuleDeselectItem),
            (ModuleMenuItem.SelectAll, ModuleSelectAllItem),
            (ModuleMenuItem.ClearSelection, ModuleClearSelectionItem)
        ];

        private void RebuildModuleContextMenu(MenuFlyout flyout)
        {
            foreach (var item in batchMenuItems)
                flyout.Items.Remove(item);

            batchMenuItems.Clear();
            contextClickThisPress = false;

            var multi = ViewModel.HasMultiContextSelection;
            var groups = ViewModel.ContextMenuGroups(contextMenuExtended);
            var shown = groups.SelectMany(group => group).ToHashSet();
            var map = ModuleMenuMap();

            foreach (var (entry, item) in map)
                item.Visibility = shown.Contains(entry) ? Visibility.Visible : Visibility.Collapsed;

            MenuFlyoutSeparator[] separators =
            [
                ModuleOrderSeparator, ModuleModSeparator, ModuleToolsSeparator,
                ModuleIdentitySeparator, ModuleSelectionSeparator
            ];

            // A separator earns its place only between two groups that both have something in them,
            // which is what stops a hidden group leaving a rule at the top of the menu or two of them
            // back to back.
            var anythingAbove = groups[0].Count > 0;

            for (var i = 1; i < groups.Count; i++)
            {
                separators[i - 1].Visibility =
                    anythingAbove && groups[i].Count > 0 ? Visibility.Visible : Visibility.Collapsed;

                anythingAbove |= groups[i].Count > 0;
            }

            // The outer ring inserts its own item into this same flyout, directly below Mark as Not on
            // Nexus, so anything the map does not name follows the group it was inserted into rather
            // than standing alone after a separator that is no longer there. That group says where a
            // copy came from, and a subscribed item came from Steam: it was published there rather than
            // on Nexus and it was not built on this machine, so the group is empty for such a row and
            // the ring's statement has nothing to add to it either.
            var declared = map.Select(pair => pair.Item).Concat<MenuFlyoutItemBase>(separators).ToHashSet();

            var revealed = ViewModel.ContextModule is not null && !multi && contextMenuExtended
                && ViewModel.ContextOffersBuiltLocally;

            foreach (var item in flyout.Items.Where(item => !declared.Contains(item)))
                item.Visibility = revealed ? Visibility.Visible : Visibility.Collapsed;

            if (!multi)
                return;

            var at = 0;

            foreach (var entry in ViewModel.MultiContextMenuEntries)
            {
                var item = new MenuFlyoutItem { Text = entry.Label, Command = entry.Command };

                ToolTipService.SetToolTip(item, entry.Tooltip);

                flyout.Items.Insert(at++, item);
                batchMenuItems.Add(item);
            }
        }

        // ContextRequested lives on a plain child Grid inside the divider template, not on the Border
        // that owns ContextFlyout: a WinUI element with ContextFlyout set marks ContextRequested
        // handled in native code (CUIElement::OnContextRequestedCore) before any app handler on that
        // same element runs, so a handler on the flyout owner itself never fires. contextDividerTarget
        // is the primary source; OnDividerContextMenuOpening also reads the flyout's own Target as a
        // second, independent path (FlyoutBase.Target is set before Opening fires) so the menu still
        // resolves the right row even if some future edit moves ContextRequested again.
        private DividerRowViewModel? contextDividerTarget;

        private void OnDividerRowContextRequested(object sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = e;

            if (sender is FrameworkElement { DataContext: DividerRowViewModel divider })
            {
                contextDividerTarget = divider;
                LogDrag($"DividerRowContextRequested captured Label='{divider.Label}'");
            }
            else
            {
                LogDrag($"DividerRowContextRequested sender did NOT match FrameworkElement+DividerRowViewModel: sender={sender?.GetType().FullName ?? "null"}");
            }
        }

        private void OnDividerContextMenuOpening(object? sender, object e)
        {
            _ = e;

            var targetDivider = (sender as MenuFlyout)?.Target is FrameworkElement { DataContext: DividerRowViewModel divider }
                ? divider
                : contextDividerTarget;

            ViewModel.ContextDivider = targetDivider;
            LogDrag($"DividerContextMenuOpening set ContextDivider={(targetDivider is null ? "null" : $"'{targetDivider.Label}'")}");
        }

        private async void OnRenameDividerClicked(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            LogDrag($"RenameDividerClicked ContextDivider={(ViewModel.ContextDivider is null ? "null" : $"'{ViewModel.ContextDivider.Label}'")}");

            if (ViewModel.ContextDivider is not { } target)
            {
                LogDrag("RenameDividerClicked returning early: ContextDivider is null");
                return;
            }

            var box = new TextBox { Text = target.Label, SelectionStart = 0, SelectionLength = target.Label.Length };

            var strings = Strings.Current;

            var dialog = new ContentDialog
            {
                Title = strings["Environment.RenameSectionDialog.Title"],
                Content = box,
                PrimaryButtonText = strings["Environment.RenameSectionDialog.PrimaryButton"],
                CloseButtonText = strings["Environment.RenameSectionDialog.CloseButton"],
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            if (await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary)
                ViewModel.RenameContextDividerCommand.Execute(box.Text);
        }

        // Click handlers rather than Command bindings, matching Rename above: the divider template's
        // x:DataType is DividerRowViewModel, which has no ViewModel property for an x:Bind to reach
        // through, and attempting it crashes MarkupCompilePass2 with no diagnostic (the same failure
        // mode the ModuleRowTemplateSelector comment at the top of this file's XAML already describes
        // for ItemTemplateSelector). Reading ViewModel.ContextDivider from code-behind sidesteps it.
        private void OnToggleDividerCollapsedClicked(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            LogDrag($"ToggleDividerCollapsedClicked ContextDivider={(ViewModel.ContextDivider is null ? "null" : $"'{ViewModel.ContextDivider.Label}'")}");
            ViewModel.ToggleContextDividerCollapsedCommand.Execute(null);
        }

        private void OnRemoveDividerClicked(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            LogDrag($"RemoveDividerClicked ContextDivider={(ViewModel.ContextDivider is null ? "null" : $"'{ViewModel.ContextDivider.Label}'")}");
            ViewModel.RemoveContextDividerCommand.Execute(null);
        }

        private void OnPreflightFixClicked(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is Button { Tag: PreflightRowViewModel row })
                row.Fix();
        }

        private void OnAcceptRiskClicked(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is not Button { Tag: PreflightRowViewModel row } button)
                return;

            row.AcceptRisk();

            // The row itself does not re-bind until the next preflight runs, deliberately: rebuilding
            // the panel from here would race a launch already in flight. This is only the same
            // immediate confirmation the dialog's own Accept button gives.
            button.Content = Strings.Current["Environment.PreflightRow.AcceptRiskButton.Accepted"];
            button.IsEnabled = false;
        }

        private void OnForgetAcceptedRiskClicked(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is Button { Tag: AcceptedRiskRowViewModel row })
                row.Forget();
        }

        // Shown only when the preflight found something that stops a mod working. Every button on it
        // leads somewhere: launching anyway is the primary and the default, so pressing Enter launches.
        // BEM never refuses a launch, and Cancel is here because the user may want it, not because BEM
        // wants them to.
        private async Task<PreflightChoice> AskAboutPreflightAsync(PreflightReport report)
        {
            var body = new StackPanel { Spacing = 12 };

            foreach (var finding in report.Prominent)
            {
                var block = new StackPanel { Spacing = 2 };

                block.Children.Add(new TextBlock
                {
                    Text = $"{finding.GradeText}: {finding.Headline}",
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Foreground = ThemeBrush("AlkSoftChampagneBrush")
                });

                block.Children.Add(new TextBlock
                {
                    Text = finding.Detail,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Foreground = ThemeBrush("AlkSteelGrayBrush")
                });

                // Warning only, same as the panel: a Critical is what the loader does, not a judgment
                // call, so muting it would only stop BEM from saying why the game still fails to start.
                //
                // Read now, act on later: accepting does not close this dialog or change what is being
                // decided this run. It only means the next preflight leaves this exact finding out of a
                // dialog like this one, until its wording changes.
                if (finding.Grade == PreflightGrade.Warning)
                {
                    var acceptButton = new Button
                    {
                        Content = Strings.Current["Environment.PreflightDialog.AcceptRiskButton"],
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0, 2, 0, 0)
                    };

                    acceptButton.Click += (_, _) =>
                    {
                        ViewModel.AcceptPreflightRisk(finding);
                        acceptButton.Content = Strings.Current["Environment.PreflightDialog.AcceptRiskButton.Accepted"];
                        acceptButton.IsEnabled = false;
                    };

                    block.Children.Add(acceptButton);
                }

                body.Children.Add(block);
            }

            body.Children.Add(new TextBlock
            {
                Text = report.Summary,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Foreground = ThemeBrush("AlkSteelGrayBrush")
            });

            var dialog = new ContentDialog
            {
                Title = Strings.Current["Environment.PreflightDialog.Title"],
                Content = new ScrollViewer { Content = body, MaxHeight = 420 },
                PrimaryButtonText = Strings.Current["Environment.PreflightDialog.PrimaryButton"],
                CloseButtonText = Strings.Current["Environment.PreflightDialog.CloseButton"],
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            // Offered only when there is something to apply, so the button is never a control that
            // does nothing.
            if (report.Fixable.Count > 0)
                dialog.SecondaryButtonText = Strings.Current.Format("Environment.PreflightDialog.SecondaryButton", report.Fixable.Count);

            return await DialogText.ShowAsync(dialog) switch
            {
                ContentDialogResult.Primary => PreflightChoice.LaunchAnyway,
                ContentDialogResult.Secondary => PreflightChoice.FixAndLaunch,
                _ => PreflightChoice.Cancel
            };
        }

        private static Microsoft.UI.Xaml.Media.Brush? ThemeBrush(string key) =>
            Application.Current.Resources.TryGetValue(key, out var found)
                ? found as Microsoft.UI.Xaml.Media.Brush
                : null;

        private void OnFixClicked(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is Button { Tag: LoadOrderIssue issue })
                ViewModel.ApplyFix(issue);
        }

        private async void OnRemoveFolderClicked(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is Button { Tag: ModuleFolderWithoutManifest folder })
                await ViewModel.RemoveFolderWithoutManifestAsync(folder);
        }

        private void OnIssueSelected(object sender, SelectionChangedEventArgs e)
        {
            _ = e;

            if (sender is not ListView { SelectedItem: IssueRowViewModel row })
                return;

            var module = ViewModel.VisibleModules.FirstOrDefault(m => m.Entry.Id == row.Issue.ModuleId);

            if (module is null)
                return;

            ModuleList.SelectedItem = module;
            ModuleList.ScrollIntoView(module);
        }

        protected override async void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            base.OnNavigatingFrom(e);

            // Leaving the page loses nothing: the load order is written as it is edited. Only a change
            // held back for a running launcher is still in memory alone.
            if (!ViewModel.HasHeldChanges)
                return;

            e.Cancel = true;

            var target = e.SourcePageType;

            if (await ConfirmDiscardAsync())
            {
                ViewModel.HasHeldChanges = false;
                _ = Frame.Navigate(target);
            }
        }

        private async Task<bool> ConfirmDiscardAsync()
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Current["Environment.DiscardChangeDialog.Title"],
                Content = Strings.Current["Environment.DiscardChangeDialog.Body"],
                PrimaryButtonText = Strings.Current["Environment.DiscardChangeDialog.PrimaryButton"],
                SecondaryButtonText = Strings.Current["Environment.DiscardChangeDialog.SecondaryButton"],
                CloseButtonText = Strings.Current["Environment.DiscardChangeDialog.CloseButton"],
                XamlRoot = XamlRoot
            };

            var choice = await DialogText.ShowAsync(dialog);

            if (choice == ContentDialogResult.Primary)
                return ViewModel.TryFlush();

            return choice == ContentDialogResult.Secondary;
        }
    }
}
