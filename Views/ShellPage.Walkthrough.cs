using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Services;
using BannerlordEnvironmentManager.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace BannerlordEnvironmentManager.Views
{
    // The first-run guide: a few short questions, one dialog each, so a new player is set up without
    // having to find Versions or Settings first. Every step can be skipped, and skipping ends the
    // guide for good; Settings can start it again.
    public sealed partial class ShellPage
    {
        private bool walkthroughRunning;
        private bool walkthroughHooked;

        private void StartWalkthroughIfFirstRun()
        {
            var settings = ShellViewModels.Instance.NavigationSettings;

            if (!walkthroughHooked)
            {
                walkthroughHooked = true;
                settings.WalkthroughRequested += (_, _) =>
                    DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _ = RunWalkthroughAsync());
            }

            if (!settings.WalkthroughDone)
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _ = RunWalkthroughAsync());
        }

        private async Task RunWalkthroughAsync()
        {
            if (walkthroughRunning)
                return;

            walkthroughRunning = true;

            try
            {
                await WaitForNoOpenDialogAsync();
                await WalkThroughAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LoggingService.LogException(ex, "The first-run guide failed");
            }
            finally
            {
                // Marked done however it ended, so a guide that failed or was skipped never comes
                // back on its own at every launch.
                ShellViewModels.Instance.NavigationSettings.SetWalkthroughDone();
                walkthroughRunning = false;
            }
        }

        // Only one ContentDialog may be open at a time, and a startup notice may be showing.
        private async Task WaitForNoOpenDialogAsync()
        {
            for (var i = 0; i < 600 && XamlRoot is not null
                 && VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count > 0; i++)
                await Task.Delay(500);
        }

        private async Task WalkThroughAsync()
        {
            var text = Strings.Current;

            // 1. Welcome.
            if (await StepAsync(text["Walkthrough.Welcome.Title"], text["Walkthrough.Welcome.Body"],
                    text["Walkthrough.Start"], null) != ContentDialogResult.Primary)
                return;

            // 2. The game BEM found, which the first refresh has already recorded as the first version.
            var install = VersionSwitcherViewModel.CurrentInstall();
            var gameBody = install is null
                ? text["Walkthrough.Game.NotFound"]
                : text.Format("Walkthrough.Game.Found", install);

            if (await StepAsync(text["Walkthrough.Game.Title"], gameBody, text["Walkthrough.Next"], null)
                != ContentDialogResult.Primary)
                return;

            // 3. Where other game versions go.
            var versions = ShellViewModels.Instance.Versions;

            if (string.IsNullOrWhiteSpace(versions.GamesRoot))
            {
                var store = new InstanceSettingsStore();
                var proposed = InstanceSettingsStore.ProposeGamesRoot();
                store.Write(store.Read() with { GamesRoot = proposed });
                versions.GamesRoot = proposed;
            }

            var rootChoice = await StepAsync(text["Walkthrough.Root.Title"],
                text.Format("Walkthrough.Root.Body", versions.GamesRoot),
                text["Walkthrough.Root.Keep"], text["Walkthrough.Root.Choose"]);

            if (rootChoice == ContentDialogResult.None)
                return;

            if (rootChoice == ContentDialogResult.Secondary)
                await versions.BrowseGamesRootCommand.ExecuteAsync(null);

            // 4. Nexus, optional, and only when BEM has no key yet.
            var settingsModel = SettingsViewModel.Instance;

            if (!settingsModel.HasNexusKey)
            {
                var nexus = await StepAsync(text["Walkthrough.Nexus.Title"], text["Walkthrough.Nexus.Body"],
                    text["Walkthrough.Nexus.SignIn"], text["Walkthrough.NotNow"]);

                if (nexus == ContentDialogResult.None)
                    return;

                // Not awaited: sign-in finishes in the browser, and the guide goes on meanwhile.
                if (nexus == ContentDialogResult.Primary)
                    _ = settingsModel.SignInWithNexusCommand.ExecuteAsync(null);
            }

            // 5. How much BEM shows.
            var view = await StepAsync(text["Walkthrough.View.Title"], text["Walkthrough.View.Body"],
                text["Walkthrough.View.Simple"], text["Walkthrough.View.Advanced"]);

            if (view == ContentDialogResult.None)
                return;

            ShellViewModels.Instance.NavigationSettings.SetAdvancedMode(view == ContentDialogResult.Secondary);

            // 6. Done: straight to Play.
            await StepAsync(text["Walkthrough.Done.Title"], text["Walkthrough.Done.Body"],
                text["Walkthrough.Done.Play"], null, showSkip: false);

            NavigateToStart(typeof(EnvironmentPage));
        }

        private async Task<ContentDialogResult> StepAsync(
            string title, string body, string primary, string? secondary, bool showSkip = true)
        {
            var dialog = new ContentDialog
            {
                Title = DialogText.Title(title),
                Content = new TextBlock
                {
                    Text = body,
                    TextWrapping = TextWrapping.Wrap,
                    Width = DialogText.ContentWidth,
                    IsTextSelectionEnabled = true
                },
                PrimaryButtonText = primary,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            if (secondary is not null)
                dialog.SecondaryButtonText = secondary;

            if (showSkip)
                dialog.CloseButtonText = Strings.Current["Walkthrough.Skip"];

            var result = await DialogText.ShowAsync(dialog);

            // The closing step has no Skip, so however it is closed it counts as finished.
            return showSkip ? result : ContentDialogResult.Primary;
        }
    }
}
