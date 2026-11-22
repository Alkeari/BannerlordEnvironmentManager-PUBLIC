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

            // The status line beside the title follows the module switches and the preflight; a
            // one-second redraw while the page is on screen is cheaper than watching every row.
            var statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            statusTimer.Tick += (_, _) => ApplyPlayStatus();
            Loaded += (_, _) =>
            {
                ApplyPlayStatus();
                ViewModel.RefreshEnabledSets();
                ViewModel.RefreshProfileSaves();
                ViewModel.RefreshProfileLaunch();
                statusTimer.Start();
            };
            Unloaded += (_, _) => statusTimer.Stop();

            // Picking a module on the folded layout shows its card; either way the side cards are laid
            // out again once the card's own binding has run.
            ViewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is { } name && SideCardInputs.Contains(name))
                {
                    DispatcherQueue.TryEnqueue(ApplySideCards);
                    return;
                }

                if (args.PropertyName != nameof(EnvironmentViewModel.HasSelectedModule))
                    return;

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (compact && ViewModel.HasSelectedModule)
                        SideTabs.SelectedKey = "Module";

                    ApplySideCards();
                });
            };

            ViewModel.PanelIssues.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplySideCards);
            Navigation.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ShellNavigationSettings.IsAdvancedMode))
                    DispatcherQueue.TryEnqueue(ApplySideCards);
            };

            // The preflight dialog is registered by the shell, not here: Launch lives in the context bar
            // on every page now, and this page may never have been opened when it is pressed.
        }

        public EnvironmentViewModel ViewModel => ShellViewModels.Instance.Environment;

        public ShellNavigationSettings Navigation => ShellViewModels.Instance.NavigationSettings;

        // The Updates popup lists the mod ids BEM holds and the pages turned down, which Library keeps.
        public InstallViewModel Library => ShellViewModels.Instance.Install;

        public string ShowHiddenLabel(int count) => Strings.Current.Plural("Environment.Preflight.ShowHidden", count);

        public Visibility HasHidden(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

        public string IgnoredLabel(int count) => Strings.Current.Plural("Environment.Preflight.IgnoredCount", count);

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
        public string MissingWorkshopTitle => Strings.Current["Environment.WorkshopMissing.Title"];
        public string ManagerActiveTitle => Strings.Current["Environment.ManagerActive.Title"];

        public string NoVersionsTitle => Strings.Current["Environment.NoVersions.Title"];

        public string NoVersionsReason => Strings.Current["Environment.NoVersions.Reason"];

        // Basic shows no cleanup button: pruning orphans is an Advanced Mode tool.
        public Visibility PruneVisibility(bool advanced, int orphanCount) =>
            PageDensity.AdvancedAnd(advanced, orphanCount > 0);

        // Basic names the counts only while something needs attention: notes alone are not Basic's business
        // and "0 errors, 0 warnings, 4 notes" is noise on the one page a player opens to press Launch.
        public string IssueMeta(bool advanced, string text, int errors, int warnings) =>
            advanced || errors > 0 || warnings > 0 ? text : string.Empty;

        public Visibility NoVersionsVisibility(bool hasInstalledVersions) =>
            hasInstalledVersions ? Visibility.Collapsed : Visibility.Visible;

        private void OnOpenVersionsRequested(object sender, RoutedEventArgs e) =>
            Frame?.Navigate(typeof(VersionsPage));

        // The selected module's card takes a share of the side column only while a module is
        // selected; otherwise Preflight and Profiles share it.
        public GridLength ModuleRowHeight(bool hasSelected) =>
            hasSelected ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        public Visibility EmptyVisibility(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public Visibility UpdatesPillVisibility(bool advanced, bool showUpdates, bool hasUpdates) =>
            !advanced && showUpdates && hasUpdates ? Visibility.Visible : Visibility.Collapsed;

        // Basic shows Preflight only while it has something to say: a finding (each with its Fix or
        // Accept the Risk), a hidden or ignored count, an undo, or copies set aside. An empty "nothing
        // found" card is Advanced's, and the load order takes the width it leaves.
        private bool PreflightHasContent =>
            ViewModel.HasPreflight
            || ViewModel.PanelIssues.Count > 0
            || ViewModel.HiddenPreflightCount > 0
            || ViewModel.CanUndoXmlRepair
            || ViewModel.HasSetAsideCopies;

        private static readonly HashSet<string> SideCardInputs =
        [
            nameof(EnvironmentViewModel.HasPreflight),
            nameof(EnvironmentViewModel.HiddenPreflightCount),
            nameof(EnvironmentViewModel.CanUndoXmlRepair),
            nameof(EnvironmentViewModel.HasSetAsideCopies),
        ];

        public Visibility NoPreflightVisibility(bool hasPreflight) =>
            hasPreflight ? Visibility.Collapsed : Visibility.Visible;

        private bool compact;

        // Below the compact width the side column is 360 px and its three cards fold into one place
        // with a tab per card (proposal 2.1), so the load order keeps the rest of the width.
        private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _ = sender;

            var nowCompact = e.NewSize.Width < 1100 - 56;

            if (nowCompact == compact && e.PreviousSize.Width > 0)
                return;

            compact = nowCompact;
            ApplySideCards();
        }

        private void OnSideTabChanged(Controls.Segmented sender, string key)
        {
            _ = sender;
            _ = key;

            if (!applyingSideCards)
                ApplySideCards();
        }

        private bool applyingSideCards;

        private (bool Compact, bool AnySide)? columnShape;

        // Which side cards show, and where. Advanced shows all three as before. Basic shows Preflight
        // only while it holds something (PreflightHasContent), and the selected module's card and the
        // Profiles card not at all: the module's actions are on its row's menu and the profiles sit
        // in the page header. With no card left the side column closes and the load order takes it.
        private void ApplySideCards()
        {
            if (applyingSideCards)
                return;

            applyingSideCards = true;

            try
            {
                var advanced = Navigation.IsAdvancedMode;
                var shows = new Dictionary<string, bool>
                {
                    ["Preflight"] = advanced || PreflightHasContent,
                    ["Module"] = advanced && ViewModel.HasSelectedModule || advanced && compact,
                    ["Profiles"] = advanced,
                };

                var anySide = shows["Preflight"] || shows["Module"] || shows["Profiles"];

                SideCards.Visibility = anySide ? Visibility.Visible : Visibility.Collapsed;

                // Only when the shape changes, so a width the splitter set is not thrown away on
                // every selection.
                if (columnShape != (compact, anySide))
                {
                    columnShape = (compact, anySide);
                    SideColumn.Width = !anySide ? new GridLength(0) : compact ? new GridLength(360) : new GridLength(1, GridUnitType.Star);
                    SideSplitter.SetFolded(compact || !anySide);
                }

                var tabs = compact && anySide && shows.Count(pair => pair.Value) > 1;
                SideTabs.Visibility = tabs ? Visibility.Visible : Visibility.Collapsed;
                SideTabsRow.Height = tabs ? GridLength.Auto : new GridLength(0);

                var wanted = new System.Collections.ObjectModel.ObservableCollection<Controls.SegmentedItem>();

                foreach (var (key, label) in new[]
                         {
                             ("Preflight", "Environment.Card.Preflight"),
                             ("Module", "Environment.Card.Module"),
                             ("Profiles", "Environment.Card.Profiles"),
                         })
                {
                    if (shows[key])
                        wanted.Add(new(key, Strings.Current[label]));
                }

                if (SideTabs.ItemsSource is not System.Collections.ObjectModel.ObservableCollection<Controls.SegmentedItem> current
                    || !current.Select(item => item.Key).SequenceEqual(wanted.Select(item => item.Key)))
                {
                    var keep = SideTabs.SelectedKey;
                    SideTabs.ItemsSource = wanted;
                    SideTabs.SelectedKey = wanted.Any(item => item.Key == keep) ? keep : wanted.FirstOrDefault()?.Key ?? "Preflight";
                }

                var cards = new (FrameworkElement Card, int WideRow, string Key)[]
                {
                    (PreflightCard, 1, "Preflight"),
                    (ModuleCard, 2, "Module"),
                    (ProfilesCard, 3, "Profiles"),
                };

                // In the folded layout a lone card fills the place without a tab row.
                var lone = compact && !tabs;

                foreach (var (card, wideRow, key) in cards)
                {
                    Grid.SetRow(card, compact ? 1 : wideRow);
                    Grid.SetRowSpan(card, compact ? 3 : 1);
                    card.Margin = compact || wideRow == 1 ? new Thickness(0) : new Thickness(0, 24, 0, 0);

                    if (!shows[key])
                        card.Visibility = Visibility.Collapsed;
                    else if (compact)
                        card.Visibility = lone || SideTabs.SelectedKey == key ? Visibility.Visible : Visibility.Collapsed;
                    else if (!ReferenceEquals(card, ModuleCard))
                        card.Visibility = Visibility.Visible;
                }

                // The module card's own binding decides it in the wide layout, in Advanced.
                if (!compact)
                    ModuleCard.Visibility = advanced && ViewModel.HasSelectedModule ? Visibility.Visible : Visibility.Collapsed;

                PreflightRow.Height = shows["Preflight"] ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                ModuleRow.Height = compact || !advanced ? new GridLength(0) : ModuleRowHeight(ViewModel.HasSelectedModule);
                ProfilesRow.Height = compact || !advanced ? new GridLength(0) : GridLength.Auto;
            }
            finally
            {
                applyingSideCards = false;
            }
        }

        // "15 of 18 modules on. 3 findings, 1 accepted." beside the title, as the mockup's status line.
        private void ApplyPlayStatus()
        {
            var modules = ViewModel.Modules;
            var on = modules.Count(row => row.IsEnabled);
            var text = Strings.Current.Format("Environment.Status.ModulesOn", on, modules.Count);

            // The list holds only open findings; the accepted ones sit in the Accepted Risks section.
            var accepted = ViewModel.IgnoredPreflightCount;
            var findings = ViewModel.PreflightFindings.Count(row => row.CanAccept) + accepted;

            if (findings > 0)
                text += " " + Strings.Current.Plural("Environment.Status.Findings", findings, accepted);

            PlayHeader.Status = text;
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

            // The list control moves only the row under the pointer. When that row is one of several
            // the player selected, the rest of the selection follows it to where it was dropped.
            var block = ViewModel.Modules.Where(row => row.IsUiSelected).ToList();
            var movedAsBlock = false;

            if (block.Count > 1
                && e.NewItems is { Count: 1 }
                && e.NewItems[0] is ModuleRowViewModel dragged
                && dragged.IsUiSelected)
            {
                var byId = moduleRows.ToDictionary(row => row.Entry.Id);

                var together = ModuleBatchOrder.Together(
                    [.. moduleRows.Select(row => row.Entry.Id)], [.. block.Select(row => row.Entry.Id)], dragged.Entry.Id);

                var gathered = together.Select(id => byId[id]).ToList();

                movedAsBlock = !gathered.SequenceEqual(moduleRows);
                moduleRows = gathered;
            }

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
            // After a block move the rows on screen still show the one-row drop, so they are rebuilt
            // from the order the block landed in.
            if (moduleOrderChanged)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    ViewModel.OnOrderChangedByUser();

                    if (movedAsBlock)
                        ViewModel.RefreshVisibleModulesPublic();
                });
            }
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

            // No pointer position means the keyboard asked, and that can arrive from the list itself, so
            // it means the selected row. A right-click on empty space still targets nothing.
            ViewModel.SetContextModule(ResolveModuleRow(e.OriginalSource)
                ?? (!e.TryGetPosition(null, out _) ? ViewModel.SelectedModule : null));
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

                // The keyboard menu key raises this from the focused item itself, which holds its row
                // as Content, so it used to open the menu for no row at all.
                if (element is ListViewItem { Content: ModuleRowViewModel focused })
                    return focused;

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

            // The keyboard menu key reaches no row handler, because the list owns the flyout and handles
            // the request first. No right press means it was the keyboard, so the focused row is the target.
            if (ViewModel.ContextModule is null && !contextClickThisPress
                && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is ListViewItem { Content: ModuleRowViewModel focused })
                ViewModel.SetContextModule(focused);

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
            (ModuleMenuItem.OrderRules, ModuleOrderRulesItem),
            (ModuleMenuItem.Update, ModuleUpdateItem),
            (ModuleMenuItem.OpenModPage, ModuleOpenModPageItem),
            (ModuleMenuItem.HoldUpdate, ModuleHoldItem),
            (ModuleMenuItem.TogglePin, ModulePinItem),
            (ModuleMenuItem.EditNotes, ModuleNotesItem),
            (ModuleMenuItem.Endorse, ModuleEndorseItem),
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

        private async void OrderRulesClicked(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ContextModule is { } target)
                await ShowLoadRulesAsync(target);
        }

        // The button at the end of a module's row.
        private void ModSettingsButtonClicked(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is FrameworkElement { DataContext: ModuleRowViewModel target })
                ModuleSettingsPanel.Show(target);
        }

        private async void LoadRulesButtonClicked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ModuleRowViewModel target })
                await ShowLoadRulesAsync(target);
        }

        // The rules that name this module, each with its Remove, and one line to add another. Built in
        // code because the list changes while the dialog is open.
        private async Task ShowLoadRulesAsync(ModuleRowViewModel target)
        {
            var strings = Strings.Current;
            var id = target.Entry.Id;

            var others = ViewModel.Modules
                .Where(row => row.Entry.Id != id)
                .OrderBy(row => row.Entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var existing = new StackPanel { Spacing = 4 };
            var message = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false };

            var direction = new ComboBox
            {
                ItemsSource = new[]
                {
                    strings["Environment.OrderRulesDialog.Before"],
                    strings["Environment.OrderRulesDialog.After"],
                    strings["Environment.OrderRulesDialog.Incompatible"]
                },
                SelectedIndex = 0
            };

            var other = new ComboBox
            {
                ItemsSource = others.Select(row => row.Entry.DisplayName).ToList(),
                PlaceholderText = strings["Environment.OrderRulesDialog.ModulePlaceholder"],
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var add = new Button { Content = strings["Environment.OrderRulesDialog.Add"] };

            void AddExisting(string description, Action removeIt)
            {
                var row = new Grid { ColumnSpacing = 8 };

                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new TextBlock
                {
                    Text = description,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsTextSelectionEnabled = false
                };

                var remove = new Button { Content = strings["Environment.OrderRulesDialog.Remove"] };

                remove.Click += (_, _) =>
                {
                    removeIt();
                    message.Text = string.Empty;
                    Refresh();
                };

                Grid.SetColumn(remove, 1);
                row.Children.Add(text);
                row.Children.Add(remove);
                existing.Children.Add(row);
            }

            void Refresh()
            {
                existing.Children.Clear();

                var rules = ViewModel.RulesNaming(id);
                var incompatible = ViewModel.IncompatibleNaming(id);

                if (rules.Count == 0 && incompatible.Count == 0)
                {
                    existing.Children.Add(new TextBlock
                    {
                        Text = strings["Environment.OrderRulesDialog.None"],
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = false
                    });
                }

                foreach (var rule in rules)
                    AddExisting(rule.Describe(), () => ViewModel.RemoveLoadOrderRule(rule));

                foreach (var pair in incompatible)
                    AddExisting(pair.Describe(), () => ViewModel.RemoveIncompatible(pair));
            }

            add.Click += (_, _) =>
            {
                if (other.SelectedIndex < 0)
                    return;

                var chosen = others[other.SelectedIndex].Entry.Id;

                // Load Before puts this module first, Load After puts the chosen one first.
                message.Text = (direction.SelectedIndex switch
                {
                    0 => ViewModel.AddLoadOrderRule(id, chosen),
                    1 => ViewModel.AddLoadOrderRule(chosen, id),
                    _ => ViewModel.AddIncompatible(id, chosen)
                }) ?? string.Empty;

                Refresh();
            };

            Refresh();

            var panel = new StackPanel { Spacing = 12, Width = DialogText.ContentWidth };

            panel.Children.Add(new TextBlock
            {
                Text = strings["Environment.OrderRulesDialog.Intro"],
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = false
            });
            panel.Children.Add(existing);
            panel.Children.Add(direction);
            panel.Children.Add(other);
            panel.Children.Add(add);
            panel.Children.Add(message);

            var dialog = new ContentDialog
            {
                Title = DialogText.Title(strings.Format("Environment.OrderRulesDialog.Title", target.Entry.DisplayName)),
                Content = panel,
                CloseButtonText = strings["Environment.OrderRulesDialog.CloseButton"],
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };

            await DialogText.ShowAsync(dialog);
        }

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
            var declared = map.Select(pair => pair.Item).Concat<MenuFlyoutItemBase>(separators).Append(ModuleOrderAndPinsItem).ToHashSet();

            // Order and Pins is the row menu's half of the column header's Order and Pins: offered for
            // one row, never while a selection stands, since a batch replaces the menu.
            ModuleOrderAndPinsItem.Visibility = ViewModel.ContextModule is not null && !multi
                ? Visibility.Visible
                : Visibility.Collapsed;

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

            var box = new TextBox
            {
                Text = target.Label,
                SelectionStart = 0,
                SelectionLength = target.Label.Length,
                MaxLength = LoadOrderDivider.MaxLabelLength
            };

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

        private void OnPreflightTurnOffClicked(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is Button { Tag: PreflightRowViewModel row })
                row.TurnOff();
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

        private void OnPutBackSetAsideClicked(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is Button { Tag: SetAsideRowViewModel row })
                row.PutBack();
        }

        // Shown only when the preflight found something that stops a mod working. Every button on it
        // leads somewhere: launching anyway is the primary and the default, so pressing Enter launches.
        // BEM never refuses a launch, and Cancel is here because the user may want it, not because BEM
        // wants them to.
        internal static async Task<PreflightChoice> AskAboutPreflightAsync(PreflightReport report, XamlRoot xamlRoot)
        {
            var viewModel = ShellViewModels.Instance.Environment;
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
                        viewModel.AcceptPreflightRisk(finding);
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
                XamlRoot = xamlRoot
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

        // An issue's severity as the finding row draws it: error, warning, or a note with no Accept.
        public static Controls.FindingSeverity SeverityOf(IssueSeverity severity) => severity switch
        {
            IssueSeverity.Error => Controls.FindingSeverity.Error,
            IssueSeverity.Warning => Controls.FindingSeverity.Warning,
            _ => Controls.FindingSeverity.Note,
        };

        private void OnIssueAcceptConfirmed(Controls.FindingRow sender, object? parameter)
        {
            _ = sender;

            if (parameter is IssueRowViewModel row)
                row.AcceptRisk();
        }

        private void OnIssueUndoRequested(Controls.FindingRow sender, object? parameter)
        {
            _ = sender;

            if (parameter is IssueRowViewModel row)
                ViewModel.UndoIssueAcceptance(row);
        }

        private void OnPreflightAcceptConfirmed(Controls.FindingRow sender, object? parameter)
        {
            _ = sender;

            if (parameter is PreflightRowViewModel row)
                row.AcceptRisk();
        }

        private void OnPreflightUndoRequested(Controls.FindingRow sender, object? parameter)
        {
            _ = sender;

            if (parameter is PreflightRowViewModel row)
                row.Undo();
        }

        private void OnPreflightIgnoreOnceRequested(Controls.FindingRow sender, object? parameter)
        {
            _ = sender;

            if (parameter is PreflightRowViewModel { Finding: { } finding })
                ViewModel.IgnorePreflightOnce(finding);
        }

        private void OnIssueAcceptRiskClicked(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (sender is Button { Tag: IssueRowViewModel row })
                row.AcceptRisk();
        }

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
