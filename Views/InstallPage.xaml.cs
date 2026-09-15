using BannerlordEnvironmentManager.Core.Install;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class InstallPage : Page
    {
        public InstallPage()
        {
            InitializeComponent();

#if DEV_BEM
            DevRing.DeveloperSurfaces.AddTo(this);
#endif
        }

        public InstallViewModel ViewModel => ShellViewModels.Instance.Install;

        public ShellNavigationSettings Navigation => ShellViewModels.Instance.NavigationSettings;

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
        }

        // The archive list is the page's primary region and keeps the height it has. The tools band
        // below it is secondary, so it is capped at a share of the page and its contents scroll
        // inside that share: a cap written as a constant number of pixels is a comfortable third of a
        // tall window and the whole of a short one. The floor keeps the band usable on a window too
        // short for a share to mean anything, where the page scrolls rather than the band vanishing.
        private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _ = sender;

            ToolsBand.MaxHeight = Math.Max(200, e.NewSize.Height * 0.32);
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
            InstallResultsSheet.MaxHeight = Math.Max(96, Math.Max(240, ActualHeight * 0.45) - barsHeight);
        }

        // One section is always chosen, so the panel never shows a picker over an empty area.
        private void OnInstallToolSelected(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
        {
            _ = e;

            var chosen = sender.SelectedItem?.Tag as string;

            foreach (var panel in new[] { ToolCoverage, ToolShaders, ToolPlatform, ToolModuleFiles, ToolBinFiles, ToolModuleFolders })
            {
                panel.Visibility = panel.Name == chosen ? Visibility.Visible : Visibility.Collapsed;
            }
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

        // These lists act on their selection, so a right-click selects what it landed on:
        // otherwise the menu would copy whatever row happened to be selected before, which is a wrong
        // answer rather than no answer. The row handlers are the reliable half, because
        // ContextRequested bubbles and the list-level one runs only if nothing in between marked the
        // event handled, while the framework shows the flyout either way.
        private void OnBinBackupRowContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = e;

            if ((sender as FrameworkElement)?.DataContext is BinBackup backup)
                ViewModel.SelectedBinBackup = backup;
        }

        private void OnReplacedModuleFolderRowContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = e;

            if ((sender as FrameworkElement)?.DataContext is ReplacedModuleFolder folder)
                ViewModel.SelectedReplacedModuleFolder = folder;
        }

        private void OnReplacedFileRowContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = e;

            if ((sender as FrameworkElement)?.DataContext is ReplacedFile replaced)
                ViewModel.SelectedReplacedFile = replaced;
        }

        private void OnConfirmedNexusIdRowContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = e;

            if ((sender as FrameworkElement)?.DataContext is ConfirmedNexusIdRow row)
                ViewModel.SelectedConfirmedNexusId = row;
        }

        // The list-level half, for a right-click that landed on a row's own padding. A click on the
        // empty space below the last row resolves to nothing and leaves the selection alone, because
        // the buttons under these lists still need it.
        private void OnBinBackupContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = sender;

            if (Resolve<BinBackup>(e.OriginalSource) is { } backup)
                ViewModel.SelectedBinBackup = backup;
        }

        private void OnReplacedModuleFolderContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = sender;

            if (Resolve<ReplacedModuleFolder>(e.OriginalSource) is { } folder)
                ViewModel.SelectedReplacedModuleFolder = folder;
        }

        private void OnReplacedFileContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = sender;

            if (Resolve<ReplacedFile>(e.OriginalSource) is { } replaced)
                ViewModel.SelectedReplacedFile = replaced;
        }

        private void OnConfirmedNexusIdContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            _ = sender;

            if (Resolve<ConfirmedNexusIdRow>(e.OriginalSource) is { } row)
                ViewModel.SelectedConfirmedNexusId = row;
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
