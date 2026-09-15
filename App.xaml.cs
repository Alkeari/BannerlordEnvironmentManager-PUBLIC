using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Modules;
using BannerlordEnvironmentManager.Core.Shutdown;
using BannerlordEnvironmentManager.Services;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.AppLifecycle;
using System.Runtime.InteropServices;
using System.Threading;
// Only the two activation payload interfaces, by name: importing the whole namespace collides with
// Microsoft.UI.Xaml's own LaunchActivatedEventArgs, which OnLaunched takes.
using ILaunchActivatedEventArgs = Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs;
using IProtocolActivatedEventArgs = Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs;

namespace BannerlordEnvironmentManager
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    partial class App
    {
        [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("shell32.dll", EntryPoint = "ExtractIconW", StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

        [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);


        private const uint WM_SETICON = 0x0080;
        private const uint ICON_BIG = 1;
        private const uint ICON_SMALL = 0;

        public static Window? AppWindow { get; private set; }

        private Window? window;

        private bool closeConfirmed;

        private bool closePromptOpen;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            LoggingService.Log("Application Initializing...");

            // Application.UnhandledException only sees exceptions that reach the XAML dispatcher. A
            // background thread that throws, or a discarded task that faults (startup fires several:
            // the archive folder load, the Nexus coverage refresh), used to bypass the dialog and the
            // log alike - the app misbehaved with a log that said nothing. Neither handler here can
            // keep the process alive in every case; what they guarantee is that no crash on any thread
            // is invisible to the log a user sends.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                LoggingService.Log($"CRITICAL ERROR (non-UI thread): {e.ExceptionObject}", LogLevel.Critical);

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                LoggingService.LogException(e.Exception, "A background task faulted with nothing awaiting it");
                e.SetObserved();
            };
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            // Log to file
#if WINDOWS
            LoggingService.LogException(e.Exception, "Unhandled Exception");
#else
            System.Diagnostics.Debug.WriteLine($"Unhandled Exception: {e.Exception.Message}");
#endif
            
            // If we have a window and content, try to show a dialog
            if (AppWindow?.Content is FrameworkElement fe && fe.XamlRoot != null)
            {
                _ = sender;
                try
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Application Error",
                        Content = $"Something went wrong, and BEM stayed open rather than closing on you:\n\n{e.Exception.Message}\n\nThe work you had on screen is still here. If the app behaves oddly from now on, restart it, and send the log.\n\nLog: {LoggingService.GetLogDirectory()}",
                        CloseButtonText = "OK",
                        XamlRoot = fe.XamlRoot
                    };

                    // Marked handled before the dialog is shown, because an unhandled XAML exception
                    // tears the process down immediately and the dialog never gets to render. That is
                    // why a crash here used to look like the window simply vanishing.
                    e.Handled = true;

                    _ = DialogText.ShowAsync(dialog);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
#if WINDOWS
                    LoggingService.LogException(ex, "Failed to show unhandled exception dialog");
#else
                    System.Diagnostics.Debug.WriteLine($"Failed to show dialog: {ex.Message}");
#endif
                    // ContentDialog refuses to open while another one is up (the close-confirmation,
                    // for one), and swallowing that here meant the app survived a crash in total
                    // silence. The Win32 box has no such rule, so the user still hears about it.
                    _ = MessageBoxW(IntPtr.Zero,
                        $"Something went wrong, and BEM stayed open rather than closing on you:\n\n{e.Exception.Message}\n\nLog: {LoggingService.GetLogDirectory()}",
                        "Application Error", 0x10);
                }
            }
            else
            {
#if WINDOWS
                LoggingService.Log($"CRITICAL ERROR (No XamlRoot): {e.Exception}", LogLevel.Critical);
#else
                System.Diagnostics.Debug.WriteLine($"CRITICAL ERROR (No XamlRoot): {e.Exception}");
#endif
            }

            // Nothing resets Handled here any more. With a window on screen the branch above keeps the
            // app alive and says so, because closing without a word is the worst of both: the user
            // loses the work and learns nothing. With no window there is nothing to keep alive and no
            // way to tell anyone, so the default of false stands and the process ends.
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            ArgumentNullException.ThrowIfNull(args);

            // A Nexus link for another game belongs to whoever held the scheme before BEM, and it must
            // reach them without BEM starting at all. Program.Main already did this before COM and
            // XAML were initialized; this repeats it for the case where OnLaunched is reached anyway.
            if (NxmLinks.TryForwardWithoutStarting())
            {
                Exit();
                return;
            }

            // A second BEM is never what anybody wanted. Clicking Mod Manager Download on Nexus starts
            // the registered handler, which is this exe, and without this every link opened another
            // whole copy: two windows, two watchers, two load orders being written to one file.
            //
            // NxmLinks has had FindLinkIn, whose comment describes reading a redirected activation's
            // command line, since before this existed. The receiving half was written and never wired
            // to anything, which is a capability nobody could reach.
            if (RedirectToTheRunningInstance())
                return;

            StartLocalizationAtStartup();

            // Decided before the shell is navigated to, so the download's page is the first one shown
            // rather than one the shell moves off again as it loads.
            if (NxmLinks.TakeLinkFromCommandLine() is { } startupLink
                && Core.Nexus.NxmLink.Parse(startupLink).IsBannerlordDownload)
                Views.ShellPage.ShowInstallPage();

            try
            {
                // Set up global exception handling
                this.UnhandledException += App_UnhandledException;

                LoggingService.Log($"Application Launched. OS Version: {Environment.OSVersion}, .NET Version: {Environment.Version}");

                // Before any page reads a canonical path: a killed session can leave a junction
                // standing at Documents, ProgramData or an AppData folder, and the first read of one
                // of those (a save, a mod log) must see the resting instance's real data, not another
                // version's. There is nothing to repair on a fresh install with no resting instance
                // yet, so this only runs once one exists.
                RepairInstancesAtStartup();

                // Steam can add a DLC to a referenced install without BEM being asked, and nothing
                // else ever revisits InstanceRecord.Dlc once it is written. Once per start is enough:
                // it is one folder check per known DLC per instance, and a DLC BEM downloads itself
                // is already recorded by SetDlc as it lands.
                ReconcileInstanceDlcAtStartup();

                // Steam patches an install it owns without BEM being asked, and RecordedGameVersion is
                // written once and never revisited: the resting instance kept saying the version it
                // was adopted at through every game update. With the DLC pass, so the record agrees
                // with its own files before anything composes a name from it.
                ReconcileInstanceGameVersionsAtStartup();

                // After the junction repair, so no folder is renamed out from under a live junction,
                // and after the DLC reconciliation, so the name is composed from a record that already
                // agrees with the files. Here rather than on demand because a folder that does not say
                // what it holds misleads in Explorer, which is where the user meets it and where BEM
                // is not running to be asked.
                AlignInstanceFolderNamesAtStartup();

                // Nexus grants single sign-on only to an application that does not take personal API
                // keys, so a key pasted into an earlier BEM is deleted rather than used, and the user is
                // told on the one start that deleted it.
                DiscardPastedNexusKeyAtStartup();

                // Set for single-file publishing
                Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);

                // Every child process BEM starts joins a job object that Windows ends when this
                // process does. DepotDownloader kills cleanly on every path BEM's own code runs on;
                // this is the one that covers BEM being closed, crashing, or being killed outright
                // while a download is running.
                ChildProcessSupervision.Registered = ChildProcessJob.Assign;

                // Without this every module scan takes each SubModule.xml at its word, and a mod that
                // declares itself official keeps the badge and its immunity from batch actions.
                if (OperatingSystem.IsWindows())
                {
                    OfficialClaimVerifier.Registered = TaleWorldsSignature.Check;

                    // The same Authenticode implementation, asked who signed a file rather than
                    // whether TaleWorlds did. The mod safety scan reports the signer per file.
                    Core.Safety.AuthenticodeVerifier.Registered = TaleWorldsSignature.Signer;
                }

                window ??= new Window();
                AppWindow = window;

                if (window.Content is not Frame rootFrame)
                {
                    rootFrame = new Frame();
                    rootFrame.NavigationFailed += OnNavigationFailed;
                    window.Content = rootFrame;
                }

                // The version in the title is the one place every screenshot and every bug report
                // carries it without the reporter having to know to look. AssemblyInformationalVersion
                // is "2.3.1+<commit>"; the hash is build provenance, not something a user should read.
                window.Title = $"Bannerlord Environment Manager {AppVersion.Display} by Alkeari Labs LLC";

#if DEV_BEM
                // Both exes carry the same version, and a window already open says nothing about the
                // file it came from, so the title bar is what answers "which one is this" without
                // opening a menu, and it is also what the taskbar shows on hover.
                window.Title += $" ({Core.Localization.Strings.Current["App.DeveloperBuildTitleSuffix"]})";
#endif

                // The window is presented before the first navigation. Navigating a brand-new shell
                // (and through it the first page) before the composition tree has its real size leaves
                // the first page realizing against a zero-sized content area, which WinUI does not
                // re-layout once a size arrives - the page stays blank until something forces a second
                // pass. That is the "first Play tab is empty, leaving and returning fixes it" failure.
                window.Activate();
                _ = rootFrame.Navigate(typeof(ShellPage), args.Arguments);

                // Window.Closed cannot be canceled and fires after teardown, so a prompt that can stop
                // the close has to hang off AppWindow.Closing, which can.
                window.AppWindow.Closing += OnAppWindowClosing;

                // Set window icon
                SetWindowIcon(window);

                // Vortex rewrites the nxm registration on every one of its own launches, so BEM
                // re-asserts on every one of its own. This also repairs the registered path after
                // the exe has been moved, which a single portable file invites.
                _ = NxmLinks.OnLaunchedAsync(window);

                StartModSafetyScan(window);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Launch exception: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        // Runs before the app's own UnhandledException handler is attached, and reads a folder,
        // reads files and parses JSON before anything else has. Failure here is never fatal: a
        // startup that cannot load a language falls back to English through LocalizationService's
        // own defaults rather than refusing to start at all.
        private static void StartLocalizationAtStartup()
        {
            try
            {
                Localization.LocalizationService.Start();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LoggingService.LogException(ex, "Failed to start localization");
            }
        }

        // The second line of defense behind ActivationSession's own restore, run once before the shell
        // is shown. Failure here is never fatal: a startup that cannot repair a junction has to open
        // anyway and let the user close the game or restart, not refuse to start at all. The result is
        // surfaced as a status message on the Versions page rather than a dialog, because it is not an
        // error the user has to act on right now.
        private static void RepairInstancesAtStartup()
        {
            try
            {
                var manager = new InstanceManager(CanonicalPathSet.ForMachine(), new InstanceSettingsStore());

                if (manager.RestingInstanceId is null)
                    return;

                var report = manager.RepairOnStartup();

                if (report.AnythingRepaired || report.Failed.Count > 0 || report.WasRefused)
                {
                    LoggingService.Log($"Startup instance repair: {report.Describe()}");
                    StartupNotices.Pending.Post(report.Describe());
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LoggingService.LogException(ex, "Failed to repair instance junctions at startup");
            }
        }

        // What each instance's own Modules folder says it carries, written back into records that
        // disagree with it. Surfaced the same way the junction repair above is, as a status message on
        // the Versions page rather than a dialog: nothing is wrong and nothing has to be acted on, but
        // a version whose name in the list has just changed should say why it changed.
        private static void ReconcileInstanceDlcAtStartup()
        {
            try
            {
                var manager = new InstanceManager(CanonicalPathSet.ForMachine(), new InstanceSettingsStore());
                var report = manager.ReconcileDlc();

                if (!report.AnythingChanged)
                    return;

                LoggingService.Log($"Startup DLC reconciliation: {report.Describe()}");
                StartupNotices.Pending.Post(report.Describe());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LoggingService.LogException(ex, "Failed to reconcile instance DLC at startup");
            }
        }

        // What the game files under each referenced instance say it is, written back into records that
        // disagree with them. Surfaced as a status message on the Versions page rather than a dialog,
        // for the same reason the DLC pass above is: nothing is wrong and nothing has to be acted on,
        // but a version whose number has just changed should say why it changed.
        private static void ReconcileInstanceGameVersionsAtStartup()
        {
            try
            {
                var manager = new InstanceManager(CanonicalPathSet.ForMachine(), new InstanceSettingsStore());
                var report = manager.ReconcileGameVersions();

                if (!report.AnythingChanged)
                    return;

                LoggingService.Log($"Startup game version reconciliation: {report.Describe()}");
                StartupNotices.Pending.Post(report.Describe());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LoggingService.LogException(ex, "Failed to reconcile instance game versions at startup");
            }
        }

        // Every instance folder renamed to say what the instance is, so the games root can be read in
        // Explorer without opening anything. A folder moving on disk is never done silently: every
        // move is named, old name and new, in the log and in the same Versions status message the two
        // repairs above use.
        //
        // Failure here is never fatal. A folder something else is holding is reported and left exactly
        // as it is, and the next start tries again.
        private static void AlignInstanceFolderNamesAtStartup()
        {
            try
            {
                var manager = new InstanceManager(CanonicalPathSet.ForMachine(), new InstanceSettingsStore());
                var report = manager.AlignFolderNames();

                if (!report.AnythingToSay)
                    return;

                LoggingService.Log($"Startup instance folder naming: {report.Describe()}");

                // Post takes the report itself, not its text: a report that moved a folder is one
                // the shell has to put in front of the user, and that decision belongs with the
                // report rather than with each caller that happens to hold one.
                StartupNotices.Pending.Post(report);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LoggingService.LogException(ex, "Failed to align instance folder names at startup");
            }
        }

        private static void DiscardPastedNexusKeyAtStartup()
        {
            if (!Core.Nexus.NexusApiKeyStore.DiscardPastedKey(Core.Nexus.NexusApiKeyStore.DefaultDirectory))
                return;

            LoggingService.Log("A Nexus API key pasted into an earlier BEM was deleted at startup.");
            StartupNotices.Pending.Post(Core.Localization.Strings.Current["Core.Nexus.ApiKeyStore.PastedKeyDiscarded"], mustBeSeen: true);
        }

        // The key is BEM's own, not the file's, so a copy of the exe moved elsewhere still finds the
        // running one. True means this process has handed its activation over and is on its way out.
        //
        // Failure here is never fatal: an app that cannot reach the instance registry has to start
        // normally rather than refuse to start at all, so a second window is the worst case rather
        // than no window.
        private static bool RedirectToTheRunningInstance()
        {
            try
            {
                // Two keys, so the developer build and the published one are two programs to Windows
                // rather than two copies of one. Sharing the key would make the developer exe hand
                // its launch to a published BEM already running and exit without a window, which
                // reads as "the dev build does not start".
#if DEV_BEM
                var running = AppInstance.FindOrRegisterForKey("Alkeari.BannerlordEnvironmentManager.Dev");
#else
                var running = AppInstance.FindOrRegisterForKey("Alkeari.BannerlordEnvironmentManager");
#endif

                if (running.IsCurrent)
                {
                    AppInstance.GetCurrent().Activated += OnActivationRedirectedHere;
                    return false;
                }

                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();

                // RedirectActivationToAsync blocks until the other instance has taken it, and awaiting
                // it on the UI thread that has to pump for the redirect deadlocks. The wait goes on a
                // worker and this thread simply stops.
                using var handed = new SemaphoreSlim(0, 1);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await running.RedirectActivationToAsync(activation);
                    }
                    finally
                    {
                        handed.Release();
                    }
                });

                handed.Wait(TimeSpan.FromSeconds(10));

                LoggingService.Log("A BEM was already running, so this launch was handed to it instead of starting a second one.");

                Environment.Exit(0);

                return true;
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Could not hand this launch to the BEM already running");
                return false;
            }
        }

        // A link that arrived while BEM was already open. It comes in as the second process's whole
        // command line rather than as an argument array, which is what FindLinkIn is for, and it
        // arrives on a background thread, so everything it touches is put back on the UI one.
        private static void OnActivationRedirectedHere(object? sender, AppActivationArguments args)
        {
            _ = sender;

            var commandLine = args?.Data switch
            {
                ILaunchActivatedEventArgs launch => launch.Arguments,
                IProtocolActivatedEventArgs protocol => protocol.Uri?.ToString(),
                _ => null
            };

            if (AppWindow is not { } window)
                return;

            _ = window.DispatcherQueue.TryEnqueue(() =>
            {
                // Brought forward first. A download that starts behind whatever the user was looking
                // at reads as the click having done nothing.
                window.Activate();

                if (NxmLinks.FindLinkIn(commandLine) is { } url)
                    NxmLinks.Handle(url, window);
            });
        }


        // Queued at low priority behind everything the first frame needs, so the window is up and
        // usable before a single file is read. The scan then runs on the thread pool.
        //
        // Nothing in here may reach the launch path: an optional feature is not allowed to make the
        // start slower, and it is certainly not allowed to stop BEM opening at all.
        private static void StartModSafetyScan(Window window)
        {
            try
            {
                window.DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        try
                        {
                            Views.ShellViewModels.Instance.ModSafety.StartBackgroundScan();
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                        {
                            LoggingService.LogException(ex, "The startup mod safety scan could not start");
                        }
                    });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LoggingService.LogException(ex, "The startup mod safety scan could not be queued");
            }
        }

        private async void OnAppWindowClosing(
            Microsoft.UI.Windowing.AppWindow sender,
            Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            _ = sender;

            var environment = Views.ShellViewModels.Instance.Environment;
            var versions = Views.ShellViewModels.Instance.VersionSwitcher;

            // Which of the two reasons to ask apply, and what one dialog says about them, is
            // ClosePromptDecision's. Both can be true at once, and two dialogs back to back is not a
            // decision point, it is an obstacle course.
            if (closeConfirmed)
                return;

            if (ClosePromptDecision.For(environment.HasHeldChanges, versions.IsDownloading) is not { } prompt)
                return;

            args.Cancel = true;

            if (closePromptOpen)
                return;

            closePromptOpen = true;

            var dialog = new ContentDialog
            {
                Title = prompt.Title,
                Content = prompt.Content,
                PrimaryButtonText = prompt.PrimaryButton,
                SecondaryButtonText = prompt.SecondaryButton,
                CloseButtonText = prompt.CloseButton,
                XamlRoot = (window?.Content as FrameworkElement)?.XamlRoot
            };

            try
            {
                var choice = await DialogText.ShowAsync(dialog);

                if (choice == ContentDialogResult.None)
                    return;

                if (choice == ContentDialogResult.Primary && prompt.PrimaryWritesLoadOrder && !environment.TryFlush())
                    return;

                // Nothing is done about the download here. It dies with this process through the job
                // object every child joins, which is what covers a crash and a kill as well as this,
                // and the bytes it has already fetched stay where Unfinished Downloads finds them.
                closeConfirmed = true;
                window?.Close();
            }
            catch (InvalidOperationException ex)
            {
                LoggingService.LogException(ex, "Failed to prompt before closing");
            }
            finally
            {
                closePromptOpen = false;
            }
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            _ = sender;
            throw new InvalidOperationException("Failed to load Page " + e.SourcePageType.FullName);
        }

        private static void SetWindowIcon(Window window)
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                var exePath = Environment.ProcessPath;

                if (!string.IsNullOrEmpty(exePath))
                {
                    var hIcon = ExtractIcon(IntPtr.Zero, exePath, 0);
                    if (hIcon != IntPtr.Zero)
                    {
                        SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, hIcon);
                        SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, hIcon);
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to set window icon: {ex.Message}");
            }
        }
    }
}

