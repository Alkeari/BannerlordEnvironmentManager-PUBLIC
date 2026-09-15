using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Nexus;
using BannerlordEnvironmentManager.Services;
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

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            LoggingService.Log("ShellPage.OnLoaded fired.");

            current = this;

            // Badge attachment touches ShellViewModels.Instance, which eagerly constructs every other
            // view model as a side effect of constructing this one. A failure in any of them used to
            // abort OnLoaded before the navigation code below ever ran, which is indistinguishable from
            // the frame simply staying empty: the nav item still looked selectable, nothing was thrown
            // where anything could catch it, and nothing was logged. Health's own badge not appearing is
            // a much smaller problem than the whole shell never navigating over it.
            try
            {
                AttachHealthBadge();
                AttachForensicsGate();
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
                    _ = AnnounceNewerBemAsync();
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
                _ = AnnounceNewerBemAsync();
            }
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
            if (AllItems().FirstOrDefault(item => item.Tag as string == "Health") is not { } navItem)
                return;

            var safety = ShellViewModels.Instance.ModSafety;
            var diagnostics = ShellViewModels.Instance.Diagnostics;

            var badge = new InfoBadge
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AlkAlertAmberBrush"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AlkBackgroundBrush"]
            };

            navItem.InfoBadge = badge;

            void ShowCount()
            {
                var count = safety.AlarmCount + diagnostics.InstallCheckFindingCount;

                badge.Value = count;
                badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;

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
                if (args.PropertyName == nameof(DiagnosticsViewModel.InstallCheckFindingCount))
                    DispatcherQueue.TryEnqueue(ShowCount);
            };

            ShowCount();

            _ = diagnostics.CheckInstallQuicklyAsync();
        }

        // Forensics is the one destination that leaves the everyday navigation: it is the most
        // investigative of the six, and the Health landing carries a Crash Reports tile in Basic so a
        // crashed everyday user still reaches answers in one click. An armed watch overrides the gate,
        // because arming is offered on Play in both modes and the page its results land on must not
        // vanish the moment someone uses it. A user already on a Forensics page when the mode flips
        // keeps the page; only the nav item goes.
        private void AttachForensicsGate()
        {
            var navigation = ShellViewModels.Instance.NavigationSettings;
            var watch = ShellViewModels.Instance.Watch;

            void Apply() => ForensicsItem.Visibility =
                navigation.IsAdvancedMode || watch.IsWatching ? Visibility.Visible : Visibility.Collapsed;

            navigation.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ShellNavigationSettings.IsAdvancedMode))
                    DispatcherQueue.TryEnqueue(Apply);
            };

            watch.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(WatchSessionViewModel.IsWatching))
                    DispatcherQueue.TryEnqueue(Apply);
            };

            // IsWatching only changes when something calls Refresh(), which happens on reaching the
            // Watch page or arming from Play, never at startup. Without this the gate read a stale
            // false on a cold start, so reopening BEM while a watch was still armed hid Forensics in
            // Basic mode until the user wandered onto a page that refreshed it. Reading the on-disk
            // state costs a run-timeline enumeration here, which is why it sits inside the caller's
            // try/catch alongside the health badge.
            watch.Refresh();

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

        private void NavigateTo(Type pageType, bool retry)
        {
            if (ContentFrame.CurrentSourcePageType == pageType)
                return;

            if (ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo()))
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
                DispatcherQueue.TryEnqueue(() => NavigateTo(pageType, retry: false));
            else
                SelectItemFor(ContentFrame.CurrentSourcePageType);
        }

        private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
        {
            _ = sender;
            SelectItemFor(e.SourcePageType);
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
        // selected alone. That is how Health and Forensics keep their highlight while a detail page
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
            "Health" => typeof(HealthPage),
            "Forensics" => typeof(ForensicsPage),
            "Saves" => typeof(SavesPage),
            "Settings" => typeof(SettingsPage),

            // The destinations a page can drill into from Health, Forensics, Library or Settings.
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
            "Unblock" => typeof(UnblockPage),
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
