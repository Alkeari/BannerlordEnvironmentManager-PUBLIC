using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Nexus;
using BannerlordEnvironmentManager.Services;
using BannerlordEnvironmentManager.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class ShellPage : Page
    {
        private static ShellPage? current;

        // Set before the shell loads when BEM is started by a Nexus link, so the page the download
        // appears on is the one showing when the window first opens.
        private static Type? requestedStartPage;

        public ShellPage()
        {
            InitializeComponent();
        }

        // Works both before the shell exists, where it decides the first page shown, and after, where
        // it moves to it. An nxm activation therefore lands the same way cold or already running.
        public static void ShowInstallPage()
        {
            if (current is { } shell)
                shell.SelectItemFor(typeof(InstallPage));
            else
                requestedStartPage = typeof(InstallPage);
        }

        // Install by dragging: an archive dropped anywhere on the window installs through the Library's
        // own install path. Anything that is not a .zip, .7z or .rar file is refused at the cursor, so
        // the drop never looks accepted when nothing will happen.
        private async void OnArchiveDragOver(object sender, DragEventArgs e)
        {
            _ = sender;

            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                return;

            var deferral = e.GetDeferral();

            try
            {
                var items = await e.DataView.GetStorageItemsAsync();

                if (items.Any(item => item is Windows.Storage.StorageFile file && InstallViewModel.IsDroppableArchive(file.Path)))
                {
                    e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                    e.DragUIOverride.Caption = Strings.Current["Install.Drop.Caption"];
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Could not read what is being dragged over the window");
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async void OnArchiveDrop(object sender, DragEventArgs e)
        {
            _ = sender;

            var deferral = e.GetDeferral();

            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = items
                    .OfType<Windows.Storage.StorageFile>()
                    .Select(file => file.Path)
                    .Where(InstallViewModel.IsDroppableArchive)
                    .ToList();

                if (paths.Count > 0)
                    await ShellViewModels.Instance.Install.InstallDroppedArchivesAsync(paths);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Installing a dropped archive failed");
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            LoggingService.Log("ShellPage.OnLoaded fired.");

            current = this;

            // Launch lives in the context bar on every page, so the preflight dialog is shown from
            // the shell, whose XamlRoot is always in the tree, rather than from Play, which may never
            // have been opened when Launch is pressed.
            ShellViewModels.Instance.Environment.AskAboutPreflight =
                report => EnvironmentPage.AskAboutPreflightAsync(report, XamlRoot);

            baseWindowTitle ??= App.AppWindow?.Title;
            ApplyDisplayMode();

            // Badge attachment touches ShellViewModels.Instance, which eagerly constructs every other
            // view model as a side effect of constructing this one. A failure in any of them used to
            // abort OnLoaded before the navigation code below ever ran, which is indistinguishable from
            // the frame simply staying empty: the nav item still looked selectable, nothing was thrown
            // where anything could catch it, and nothing was logged. Health's own badge not appearing is
            // a much smaller problem than the whole shell never navigating over it.
            try
            {
                AttachHealthBadge();
                AttachAdvancedModeText();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LoggingService.LogException(ex, "AttachHealthBadge failed; continuing without it");
            }

            var start = requestedStartPage;
            requestedStartPage = null;

            // Setting SelectedItem alone does not navigate: NavigationView raises ItemInvoked only on a
            // user click, and programmatic selection raises SelectionChanged, which this shell does not
            // wire to navigation. The first page therefore rendered blank until the user clicked a real
            // tab. The frame is navigated here, the same way a click would, and the item is selected to
            // match.
            //
            // A single deferred dispatcher tick used to stand in for "the shell has laid out." On a cold
            // launch the window's real client size can still be settling (DPI negotiation, first paint)
            // when that tick fires, so ContentFrame was still zero-sized, the first page realized against
            // it, and it stayed blank until something forced a second layout pass. Waiting for
            // ContentFrame to actually report a size closes that race instead of guessing at a delay.
            var sized = ContentFrame.ActualWidth > 0 && ContentFrame.ActualHeight > 0;

            LoggingService.Log(
                $"Shell loaded: ContentFrame is {(sized ? "already sized" : "zero-sized, waiting for SizeChanged")} "
                + $"({ContentFrame.ActualWidth}x{ContentFrame.ActualHeight}).");

            if (sized)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    NavigateToStart(start);
                    ShowWhatMustBeSeen();
                    StartWalkthroughIfFirstRun();
                    _ = AnnounceNewerBemAsync();
                    _ = EnsureEnvironmentLoadedAsync();
                });
            }
            else
            {
                ContentFrame.SizeChanged += OnContentFrameFirstSized;
            }

            void OnContentFrameFirstSized(object? sizeSender, SizeChangedEventArgs args)
            {
                _ = sizeSender;

                if (ContentFrame.ActualWidth <= 0 || ContentFrame.ActualHeight <= 0)
                    return;

                ContentFrame.SizeChanged -= OnContentFrameFirstSized;

                LoggingService.Log(
                    $"ContentFrame first sized to {ContentFrame.ActualWidth}x{ContentFrame.ActualHeight}, navigating.");

                NavigateToStart(start);
                ShowWhatMustBeSeen();
                StartWalkthroughIfFirstRun();
                _ = AnnounceNewerBemAsync();
                _ = EnsureEnvironmentLoadedAsync();
            }
        }

        // The context bar's version, target and Launch read the Play view model on every page, and BEM
        // can now open on another page than Play, so the module list and the launch targets are read at
        // startup the same way Play reads them when it opens, rather than waiting for a visit to Play.
        private async Task EnsureEnvironmentLoadedAsync()
        {
            // Play reads all of this itself when it opens; doing it twice at once would race two reads.
            if (ContentFrame.CurrentSourcePageType == typeof(EnvironmentPage))
                return;

            try
            {
                var environment = ShellViewModels.Instance.Environment;
                var install = ShellViewModels.Instance.Install;

                if (!string.IsNullOrWhiteSpace(install.GameInstallPath))
                    environment.GameInstallPath = install.GameInstallPath;

                environment.RefreshCompanionState();

                if (environment.Modules.Count == 0)
                    await environment.Refresh();

                if (string.IsNullOrWhiteSpace(install.GameInstallPath)
                    && !string.IsNullOrWhiteSpace(environment.GameInstallPath))
                {
                    install.GameInstallPath = environment.GameInstallPath;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LoggingService.LogException(ex, "Could not read the load order at startup");
            }
        }

        // Opens the slide-in detail panel over whatever page is showing.
        public static void ShowDetail(string title, UIElement content)
        {
            if (current is not { } shell)
                return;

            shell.DetailTitle.Text = title;
            ToolTipService.SetToolTip(shell.DetailTitle, title);
            shell.DetailContent.Content = content;
            shell.DetailOverlay.Visibility = Visibility.Visible;
            shell.SlideDetail(fromOffset: shell.DetailColumn.ActualWidth > 0 ? shell.DetailColumn.ActualWidth : 640, toOffset: 0, then: null);
            shell.DetailPanel.Focus(FocusState.Programmatic);
        }

        private void CloseDetail() =>
            SlideDetail(fromOffset: 0, toOffset: DetailPanel.ActualWidth, then: () =>
            {
                DetailOverlay.Visibility = Visibility.Collapsed;
                DetailContent.Content = null;
            });

        // 240 ms ease-out (design system 6); none at all when Windows asks for reduced motion.
        private void SlideDetail(double fromOffset, double toOffset, Action? then)
        {
            if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            {
                DetailSlide.X = toOffset;
                then?.Invoke();
                return;
            }

            var animation = new DoubleAnimation
            {
                From = fromOffset,
                To = toOffset,
                Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };

            Storyboard.SetTarget(animation, DetailSlide);
            Storyboard.SetTargetProperty(animation, "X");

            var story = new Storyboard();
            story.Children.Add(animation);
            story.Completed += (_, _) => then?.Invoke();
            story.Begin();
        }

        private void OnDetailClose(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            CloseDetail();
        }

        private void OnDetailScrimTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            CloseDetail();
        }

        protected override void OnKeyDown(Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape && DetailOverlay.Visibility == Visibility.Visible)
            {
                CloseDetail();
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }

        private string? announcedVersion;

        private static BemUpdateStateStore BemUpdateStore => new(
            Path.Combine(NexusApiKeyStore.DefaultDirectory, BemUpdateStateStore.FileName));

        // Asked of Nexus's public API, so it needs no key and works for everyone. A check that finds
        // nothing, or cannot reach Nexus, shows nothing and is only logged: a start is never the place
        // to report that a courtesy check failed.
        private async Task AnnounceNewerBemAsync()
        {
            try
            {
                var options = new NexusOptionsStore(
                    Path.Combine(NexusApiKeyStore.DefaultDirectory, NexusOptionsStore.FileName)).Load();

                if (!options.BemUpdateCheckAtStartup)
                    return;

                var store = BemUpdateStore;
                var state = store.Load();
                var now = DateTimeOffset.UtcNow;

                if (BemUpdateNotice.IsCheckDue(state, now))
                {
                    var published = await BemUpdateCheck.FetchPublishedAsync(new NexusHttpTransport(), CancellationToken.None);

                    if (published.Found)
                    {
                        state = BemUpdateNotice.Checked(state, published.Version!, now);
                        store.Save(state);
                    }
                    else
                    {
                        LoggingService.Log(
                            $"The startup check for a newer BEM got no version from Nexus (status {published.StatusCode}"
                            + $"{(published.TransportError is { } error ? $", {error}" : string.Empty)}).",
                            LogLevel.Warn);
                    }
                }

                if (BemUpdateNotice.VersionToAnnounce(state, AppVersion.Display, now) is not { } version)
                    return;

                announcedVersion = version;
                BemUpdateText.Text = Strings.Current.Format("Shell.BemUpdate.Available", version, AppVersion.Display);
                BemUpdateBar.Visibility = Visibility.Visible;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LoggingService.LogException(ex, "The startup check for a newer BEM failed");
            }
        }

        private async void OnBemUpdateOpenPage(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            try
            {
                _ = await Windows.System.Launcher.LaunchUriAsync(new Uri(BemUpdateCheck.PageUrl));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LoggingService.LogException(ex, "Could not open BEM's Nexus page");
            }
        }

        private void OnBemUpdateSkip(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            if (announcedVersion is { } version)
                BemUpdateStore.Save(BemUpdateNotice.Skipped(BemUpdateStore.Load(), version));

            BemUpdateBar.Visibility = Visibility.Collapsed;
        }

        private void OnBemUpdateClose(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            BemUpdateStore.Save(BemUpdateNotice.Dismissed(BemUpdateStore.Load(), DateTimeOffset.UtcNow));
            BemUpdateBar.Visibility = Visibility.Collapsed;
        }

        // Anything a startup pass changed on the user's own disk, said to their face on the one start
        // that changed it. The Versions status line carries the same text, but it sits at the foot of
        // one page among seven: five instance folders were once renamed and, standing on
        // Play, the user was told nothing. Enqueued behind the first navigation so it opens over a shell
        // that has already drawn a page, and shown once because the board hands its text over once.
        private void ShowWhatMustBeSeen()
        {
            var notice = StartupNotices.Pending.TakeWhatMustBeSeen();

            if (notice.Length == 0)
                return;

            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _ = SayAsync(notice));
        }

        // Failure here is never fatal, but it must never be silent either: a dialog that could not
        // open leaves the log as the only record of a folder that moved, and that is the defect this
        // exists to close.
        private async Task SayAsync(string notice)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = DialogText.Title(Strings.Current["Shell.StartupNotice.Dialog.Title"]),
                    Content = new TextBlock
                    {
                        Text = notice,
                        TextWrapping = TextWrapping.Wrap,
                        Width = DialogText.ContentWidth,
                        IsTextSelectionEnabled = true
                    },
                    CloseButtonText = Strings.Current["Shell.StartupNotice.Dialog.Close"],
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot
                };

                await DialogText.ShowAsync(dialog);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LoggingService.LogException(ex, $"Failed to show the startup notice: {notice}");
            }
        }

        private void NavigateToStart(Type? start)
        {
            // With no page asked for, BEM opens where the user left it (proposal 1.1), as long as that
            // was a destination of its own and is still shown.
            start ??= ShellViewModels.Instance.NavigationSettings.LastPage is { } last
                && PageTypeFor(last) is { } lastType
                && ItemFor(lastType) is { Visibility: Visibility.Visible }
                    ? lastType
                    : null;

            var item = start is null ? Navigation.MenuItems[0] : ItemFor(start) ?? Navigation.MenuItems[0];

            if (item is not NavigationViewItem { Tag: string tag })
                return;

            // Selecting first and navigating second keeps the highlight on the right item: the frame's
            // Navigated handler also selects, but navigating through code after the item is highlighted
            // leaves no window where the wrong item is lit.
            Navigation.SelectedItem = item;

            if (PageTypeFor(tag) is { } pageType)
                NavigateTo(pageType);
        }

        // The health count is the whole notification on the Health destination: what a scan found
        // that needs reading. It sums the mod-safety alarm count and the fast install-check count, the
        // two checks that run without a crash report, and stays visible from outside the page.
        private void AttachHealthBadge()
        {
            if (AllItems().FirstOrDefault(item => item.Tag as string == "Troubleshoot") is not { } navItem)
                return;

            var safety = ShellViewModels.Instance.ModSafety;
            var diagnostics = ShellViewModels.Instance.Diagnostics;
            var navigation = ShellViewModels.Instance.NavigationSettings;

            var badge = new InfoBadge
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AlkAlertAmberBrush"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AlkBackgroundBrush"]
            };

            navItem.InfoBadge = badge;

            void ShowCount()
            {
                // Open warnings and errors counted for the Health badge, excluding Notes and accepted findings.
                var count = safety.AlarmCount + diagnostics.InstallCheckFindingCount
                    + diagnostics.XmlOverlapConflicts.Count(row => !row.Accepted && !row.IsExpected);

                badge.Value = count;
                badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;

                // Basic lists Troubleshoot only while it has something to fix, or while it is open.
                navItem.Visibility = navigation.IsAdvancedMode || count > 0 || ContentFrame.Content is TroubleshootPage
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                ToolTipService.SetToolTip(
                    navItem,
                    count > 0
                        ? Strings.Current.Plural("Shell.Nav.Health.BadgeTooltip", count)
                        : Strings.Current["Shell.Nav.Health.BadgeTooltip.Clean"]);
            }

            safety.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ModSafetyViewModel.AlarmCount))
                    DispatcherQueue.TryEnqueue(ShowCount);
            };

            diagnostics.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(DiagnosticsViewModel.InstallCheckFindingCount)
                    or nameof(DiagnosticsViewModel.AcceptedXmlOverlapCount))
                    DispatcherQueue.TryEnqueue(ShowCount);
            };

            diagnostics.XmlOverlapConflicts.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(ShowCount);
            diagnostics.XmlOverlapOverrides.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(ShowCount);
            diagnostics.Collisions.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(ShowCount);

            navigation.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ShellNavigationSettings.IsAdvancedMode))
                    DispatcherQueue.TryEnqueue(ShowCount);
            };

            ContentFrame.Navigated += (_, _) => DispatcherQueue.TryEnqueue(ShowCount);

            ShowCount();
            AttachVersionsVisibility();

            _ = diagnostics.CheckInstallQuicklyAsync();
        }

        // Basic lists Versions only when there is a choice of versions to manage: none yet (so one can be
        // added) or two and more. With exactly one, the destination has nothing everyday to offer.
        private void AttachVersionsVisibility()
        {
            var navigation = ShellViewModels.Instance.NavigationSettings;
            var versions = ShellViewModels.Instance.Environment.VersionSwitcher.InstalledVersions;

            void Apply() => VersionsItem.Visibility =
                navigation.IsAdvancedMode || versions.Count != 1 || ContentFrame.Content is VersionsPage
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            versions.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(Apply);
            navigation.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ShellNavigationSettings.IsAdvancedMode))
                    DispatcherQueue.TryEnqueue(Apply);
            };
            ContentFrame.Navigated += (_, _) => DispatcherQueue.TryEnqueue(Apply);

            Apply();
        }

        private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            _ = sender;

            if (args.InvokedItemContainer is not NavigationViewItem { Tag: string tag })
                return;

            // ItemInvoked fires on every click, including a click on the already-highlighted item.
            // That is what lets a drill-in page (an Install Check opened from Health) get back to its
            // landing: re-clicking Health must take you to Health, not sit on the subpage. Selection
            // changed would never fire for that click.
            if (PageTypeFor(tag) is not { } pageType)
            {
                LoggingService.Log(
                    $"The navigation item tagged '{tag}' has no page, so nothing was shown for it.",
                    LogLevel.Error);
                return;
            }

            NavigateTo(pageType);
        }

        // The one place a destination becomes page content. Both a click and the initial selection go
        // through here, so the first page on launch navigates exactly the way a click does.
        private void NavigateTo(Type pageType) => NavigateTo(pageType, retry: true);

        // A page that shows several views (Troubleshoot's tabs, Settings' tools) opens
        // the named one when it is navigated to with that view as the parameter, or when it is already
        // on screen and asked to.
        public interface IHostsViews
        {
            void ShowView(string view);
        }

        // Opens a page from anywhere, routing a page that now lives inside another to its owner.
        public static void Open(Type page, object? parameter = null)
        {
            if (current is not { } shell)
                return;

            shell.DispatcherQueue.TryEnqueue(() => shell.OpenCore(page, parameter));
        }

        private void OpenCore(Type page, object? parameter)
        {
            if (PageHost.OwnerOf(page) is { } owner)
            {
                page = owner.Owner;
                parameter = owner.View;
            }

            if (ContentFrame.Content is IHostsViews host && ContentFrame.CurrentSourcePageType == page && parameter is string view)
            {
                host.ShowView(view);
                return;
            }

            SelectItemFor(page);
            NavigateTo(page, retry: true, parameter);
        }

        // A navigation anywhere in BEM to a page that now lives inside another lands on its owner.
        private void OnContentFrameNavigating(object sender, NavigatingCancelEventArgs e)
        {
            _ = sender;

            if (PageHost.OwnerOf(e.SourcePageType) is null)
                return;

            e.Cancel = true;
            Open(e.SourcePageType, e.Parameter);
        }

        private void NavigateTo(Type pageType, bool retry, object? parameter = null)
        {
            if (ContentFrame.CurrentSourcePageType == pageType && parameter is null)
                return;

            bool navigated;

            using (UiStallMonitor.Op($"Navigate to {pageType.Name}"))
                navigated = ContentFrame.Navigate(pageType, parameter, new EntranceNavigationTransitionInfo());

            if (navigated)
                return;

            // Navigate can decline with no exception and no NavigationFailed event, which used to leave
            // the nav item highlighted over an untouched, still-empty ContentFrame with nothing in the
            // log to say why. One retry a frame later is what re-clicking the same item by hand does,
            // and it costs nothing when the first attempt would have succeeded anyway.
            LoggingService.Log(
                $"ContentFrame.Navigate to {pageType.Name} returned false (retry={retry}). "
                + $"Current source page: {ContentFrame.CurrentSourcePageType?.Name ?? "null"}, "
                + $"size {ContentFrame.ActualWidth}x{ContentFrame.ActualHeight}.",
                LogLevel.Warn);

            if (retry)
                DispatcherQueue.TryEnqueue(() => NavigateTo(pageType, retry: false, parameter));
            else
                SelectItemFor(ContentFrame.CurrentSourcePageType);
        }

        private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
        {
            _ = sender;
            SelectItemFor(e.SourcePageType);

            // Launch Game is the page's primary action on Play alone; everywhere else the page's own
            // action is the one primary of its region.
            Context.IsLaunchPrimary = e.SourcePageType == typeof(EnvironmentPage);

            ApplyWindowTitle(e.SourcePageType);

            if (ItemFor(e.SourcePageType) is { Tag: string tag })
                ShellViewModels.Instance.NavigationSettings.SetLastPage(tag);
        }

        private string? baseWindowTitle;

        // The page in the window title (design system 7.17), ahead of the name and version so the
        // taskbar shows which page is open, and the full title still reads as before after it.
        private void ApplyWindowTitle(Type? pageType)
        {
            if (App.AppWindow is not { } window || baseWindowTitle is null)
                return;

            window.Title = TitleKeyFor(pageType) is { } key
                ? Strings.Current.Format("Shell.WindowTitle", Strings.Current[key], baseWindowTitle)
                : baseWindowTitle;
        }

        private static string? TitleKeyFor(Type? pageType) => pageType?.Name switch
        {
            nameof(EnvironmentPage) => "Environment.Title",
            nameof(VersionsPage) => "Versions.Title",
            nameof(InstallPage) => "Install.Title",
            nameof(TroubleshootPage) => "Troubleshoot.Title",
            nameof(SavesPage) => "Saves.Title",
            nameof(SettingsPage) => "Settings.Title",
            nameof(DiagnosticsPage) => "Diagnostics.Title",
            nameof(DiagnosticLogsPage) => "Logs.Title",
            nameof(DiagnosticFilesPage) => "Files.Title",
            nameof(InstallChecksPage) => "InstallChecks.Title",
            nameof(ModOverlapsPage) => "Overlaps.Title",
            nameof(ModSafetyPage) => "Safety.Title",
            nameof(BootChecksPage) => "BootChecks.Title",
            nameof(SubModuleValidatorPage) => "Validator.Title",
            nameof(ModSettingsPage) => "ModSettings.Title",
            nameof(WatchPage) => "Watch.Title",
            nameof(ToolkitPage) => "Toolkit.Title",
            _ => null
        };

        private void OnDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
        {
            _ = sender;
            _ = args;
            ApplyDisplayMode();
        }

        // Below the compact width the sidebar is the 56 px icon rail: the brand and the mode line have
        // no room, every item keeps its icon and its tooltip, and the context bar drops its labels.
        private void ApplyDisplayMode()
        {
            var expanded = Navigation.DisplayMode == NavigationViewDisplayMode.Expanded;

            Brand.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            AdvancedModeHeader.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            Context.IsCompact = !expanded;
        }

        private void AttachAdvancedModeText()
        {
            var navigation = ShellViewModels.Instance.NavigationSettings;

            void Apply() => AdvancedModeText.Text = Strings.Current[
                navigation.IsAdvancedMode ? "Shell.AdvancedMode.On" : "Shell.AdvancedMode.Off"];

            navigation.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ShellNavigationSettings.IsAdvancedMode))
                    DispatcherQueue.TryEnqueue(Apply);
            };

            Apply();
        }

        private void OnOpenVersionsRequested(object? sender, EventArgs e)
        {
            _ = sender;
            _ = e;
            SelectItemFor(typeof(VersionsPage));
            NavigateTo(typeof(VersionsPage));
        }

        // With nothing wired here, a page that throws while constructing or loading used to fail this
        // silently: no exception reaches App's own UnhandledException handler, nothing is logged, and
        // ContentFrame is simply left as it was, which for the first navigation on launch is empty.
        // Handled rather than left to crash the app: this is recoverable the same way NavigateTo's own
        // retry is, by trying again rather than tearing the window down over one failed page.
        private void OnContentFrameNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            _ = sender;

            LoggingService.LogException(
                e.Exception, $"ContentFrame navigation to {e.SourcePageType?.FullName} failed");

            e.Handled = true;
        }

        private void SelectItemFor(Type? pageType)
        {
            if (ItemFor(pageType) is { } item && !ReferenceEquals(Navigation.SelectedItem, item))
                Navigation.SelectedItem = item;
        }

        // A drill-in page without its own nav item has no item to select, so null leaves whatever is
        // selected alone. That is how Troubleshoot keeps its highlight while a detail page
        // is open.
        private NavigationViewItem? ItemFor(Type? pageType) =>
            pageType is null
                ? null
                : AllItems().FirstOrDefault(item => item.Tag is string tag && PageTypeFor(tag) == pageType);

        private IEnumerable<NavigationViewItem> AllItems() =>
            Navigation.MenuItems.Concat(Navigation.FooterMenuItems)
                .OfType<NavigationViewItem>();

        private static Type? PageTypeFor(string tag) => tag switch
        {
            "Environment" => typeof(EnvironmentPage),
            "Versions" => typeof(VersionsPage),
            "Install" => typeof(InstallPage),
            "Troubleshoot" => typeof(TroubleshootPage),

            // The two destinations it replaced, kept so a last-opened page saved by an earlier version
            // still reopens on the page that holds what they held.
            "Health" or "Forensics" => typeof(TroubleshootPage),
            "Saves" => typeof(SavesPage),
            "Settings" => typeof(SettingsPage),

            // The destinations a page can drill into from Troubleshoot, Library or Settings.
            // They have no nav item of their own, so selecting one highlights nothing, but navigating
            // straight to the type still works from code.
            "Diagnostics" => typeof(DiagnosticsPage),
            "DiagnosticLogs" => typeof(DiagnosticLogsPage),
            "DiagnosticFiles" => typeof(DiagnosticFilesPage),
            "InstallChecks" => typeof(InstallChecksPage),
            "ModOverlaps" => typeof(ModOverlapsPage),
            "ModSafety" => typeof(ModSafetyPage),
            "BootChecks" => typeof(BootChecksPage),
            "Validator" => typeof(SubModuleValidatorPage),
            "ModSettings" => typeof(ModSettingsPage),
            "Watch" => typeof(WatchPage),
            "Toolkit" => typeof(ToolkitPage),

            // Null rather than a page. Falling back to Settings meant a mistyped Tag showed a working
            // screen for the wrong destination, which reads as "that tab does nothing" instead of
            // naming the fault.
            _ => null
        };
    }
}
