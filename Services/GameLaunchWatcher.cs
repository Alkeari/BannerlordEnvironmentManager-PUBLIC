using System.Diagnostics;
using System.Threading;
using BannerlordEnvironmentManager.Core.Game;
using BannerlordEnvironmentManager.Core.Localization;
using Microsoft.UI.Dispatching;

namespace BannerlordEnvironmentManager.Services
{
    /// <summary>
    /// Sees a Bannerlord process start, whoever started it. With Keep BEM Watching on, each one gets the
    /// preparation BEM's own Launch does; the first one BEM did not start also gets the one-time offer
    /// to turn the watcher on. The detection rules are Core's <see cref="GameLaunchDetector"/>; this
    /// only lists the running processes once a second on a low-priority thread, which needs no
    /// administrator rights, unlike a WMI process-start trace.
    /// </summary>
    internal sealed class GameLaunchWatcher : IDisposable
    {
        private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(1);

        private readonly EnvironmentViewModel environment;
        private readonly DispatcherQueue ui;
        private readonly GameLaunchDetector detector = new();

        // A launcher and then the game start within seconds of each other and are one launch.
        private readonly RefreshThrottle oneLaunch = new(TimeSpan.FromSeconds(30));
        private readonly CancellationTokenSource stop = new();
        private bool offering;

        public GameLaunchWatcher(EnvironmentViewModel environment, DispatcherQueue ui)
        {
            this.environment = environment;
            this.ui = ui;
        }

        public void Start()
        {
            var thread = new Thread(() => Watch(stop.Token))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "Game launch watcher"
            };

            thread.Start();
        }

        public void Dispose() => stop.Cancel();

        private void Watch(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    foreach (var launch in detector.Poll(Snapshot()))
                    {
                        var seen = launch;
                        ui.TryEnqueue(async () => await OnLaunchAsync(seen));
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    LoggingService.LogException(ex, "The game launch watcher failed to list processes");
                }

                if (token.WaitHandle.WaitOne(PollEvery))
                    return;
            }
        }

        private static List<RunningProcess> Snapshot()
        {
            var found = new List<RunningProcess>();

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName;

                    if (GameLaunchDetector.IsGameProcess(name))
                        found.Add(new RunningProcess(process.Id, name));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Exited between the listing and the read.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return found;
        }

        // On the UI thread: a game BEM started itself (its Launch, a dry run, a bisection) is not a launch
        // to prepare or offer anything for; BEM's own paths already did it.
        private async Task OnLaunchAsync(DetectedLaunch launch)
        {
            try
            {
                if (environment.BemOwnsTheRunningGame)
                    return;

                if (environment.TrayWatcher.ShouldOffer(launch) && !offering)
                    await OfferAsync();

                if (!environment.KeepWatchingEnabled || !oneLaunch.TryBegin())
                    return;

                try
                {
                    LoggingService.Log($"Preparing for a game started outside BEM ({launch.Name}).");
                    await environment.PrepareForeignLaunchAsync(launch.Name);
                }
                finally
                {
                    oneLaunch.Finish();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LoggingService.LogException(ex, "Preparing for a game started outside BEM failed");
            }
        }

        private async Task OfferAsync()
        {
            offering = true;

            try
            {
                var dialog = new ContentDialog
                {
                    Title = Strings.Current["Watcher.Offer.Title"],
                    Content = Strings.Current["Watcher.Offer.Content"],
                    PrimaryButtonText = Strings.Current["Watcher.Offer.TurnOn"],
                    CloseButtonText = Strings.Current["Watcher.Offer.NotNow"],
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = (App.AppWindow?.Content as FrameworkElement)?.XamlRoot
                };

                if (dialog.XamlRoot is null)
                    return;

                // Settled before it is shown: an offer made once is made once, even if the dialog is
                // dismissed by closing the window.
                environment.SettleWatcherOffer(turnOn: false);
                var choice = await DialogText.ShowAsync(dialog);

                if (choice == ContentDialogResult.Primary)
                    environment.SettleWatcherOffer(turnOn: true);
            }
            finally
            {
                offering = false;
            }
        }
    }
}
