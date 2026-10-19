using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Core.Nexus;

namespace BannerlordEnvironmentManager.Services
{
    // BEM's side of the nxm:// scheme.
    //
    // Windows launches the registered handler with the whole URL as an argument. A Bannerlord
    // download is fetched into the folder BEM already watches for archives; everything else is
    // handed straight to whoever held the scheme before BEM, without BEM's window ever appearing,
    // because a Skyrim download that opens a Bannerlord manager is a bug even when it works.
    public static class NxmLinks
    {
        private static readonly Lazy<NxmHandler> LazyHandler = new(() => new NxmHandler(
            new WindowsNxmRegistry(),
            new NxmSettingsStore(Path.Combine(NexusApiKeyStore.DefaultDirectory, NxmSettingsStore.FileName)),
            Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location ?? string.Empty));

        public static NxmHandler Handler => LazyHandler.Value;

        public static string? LastActivationMessage { get; private set; }

        public static string? TakeLinkFromCommandLine() =>
            Environment.GetCommandLineArgs()
                .Skip(1)
                .FirstOrDefault(argument => argument.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));

        // A redirected activation arrives as the second instance's whole command line rather than as
        // an argument array, so the link has to be picked out of it.
        public static string? FindLinkIn(string? commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return null;

            foreach (var token in commandLine.Split([' ', '\t', '"'], StringSplitOptions.RemoveEmptyEntries))
                if (token.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
                    return token;

            return null;
        }

        // Called before any window exists. A link that is not BEM's never causes BEM to start.
        public static bool TryForwardWithoutStarting()
        {
            if (TakeLinkFromCommandLine() is not { } url)
                return false;

            var link = NxmLink.Parse(url);

            if (link.IsBannerlordDownload)
                return false;

            var route = Handler.Route(link);

            if (route.Action != NxmAction.Forward
                || NxmRouting.BuildForward(route.ForwardCommand, url) is not { } forward)
                return false;

            try
            {
                Process.Start(new ProcessStartInfo(forward.Executable, forward.Arguments) { UseShellExecute = false, CreateNoWindow = true });

                // The redacted form, because the download grant in the query is a credential.
                LoggingService.Log($"Forwarded a Nexus link to {forward.Executable}: {link.Redacted}");

                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                LoggingService.LogException(ex, "Failed to forward a Nexus link to the previous handler");
                return false;
            }
        }

        // Called once the window is up: deals with a link BEM was started by, then re-asserts the
        // registration and says so the first time only. The link goes first because the announcement
        // is a modal dialog and a download the user just asked for should not wait behind it.
        public static async Task OnLaunchedAsync(Window window)
        {
            if (TakeLinkFromCommandLine() is { } url)
                Handle(url, window);

            if (!Handler.Settings.HandlerEnabled)
                return;

            var result = Handler.Reassert();

            if (result.ShouldAnnounce)
                await AnnounceAsync(window, result.Message);
        }

        // The single entry point for a link, whether BEM was started by it or was already running and
        // had the activation redirected into it. Both go through here so the two behave identically.
        public static void Handle(string url, Window window)
        {
            var link = NxmLink.Parse(url);

            if (link.IsBannerlordDownload)
            {
                Views.ShellPage.ShowInstallPage();
                _ = ConfirmAndDownloadAsync(link, window);
                return;
            }

            // Forwarding was already attempted before any window existed. Reaching here means it had
            // nowhere to go, or the previous handler could not be started, and either way the user is
            // told rather than left with a link that vanished.
            var route = Handler.Route(link);

            _ = AnnounceAsync(
                window,
                route.Action == NxmAction.NoHandler
                    ? route.Reason
                    : Strings.Current.Format("Install.NexusLink.Failed.Content", route.Reason),
                Strings.Current["Install.NexusLink.Failed.Title"]);
        }

        // An nxm:// link arrives machine-wide. The browser knows nothing about which version BEM has
        // selected, so the destination is named and confirmed rather than assumed, which is what the
        // design asks for: a link clicked while a managed version is playing may well have been meant
        // for the other version, and the user is the only one who knows which.
        //
        // The download itself extracts nothing - the archive lands in the folder BEM watches and
        // installing it is a separate press on Library - so a dialog that cannot be shown at all
        // (no XamlRoot yet on a cold start, or another dialog already up) lets the download run and
        // says so in the log, rather than dropping a link the user just clicked.
        private static async Task ConfirmAndDownloadAsync(NxmLink link, Window window)
        {
            var install = Views.ShellViewModels.Instance.Install;

            // The guided BUTR stack walk opened this exact mod's page a moment ago and already named
            // the destination when it started, so confirming it again five times in a row would be
            // asking the same question the walk was started to answer.
            if (install.IsButrStackWaitingFor(link))
            {
                await install.ContinueButrStackAsync(link);
                return;
            }

            if ((window.Content as FrameworkElement)?.XamlRoot is { } root)
            {
                var dialog = new ContentDialog
                {
                    Title = Strings.Current["Install.NexusLink.Confirm.Title"],
                    Content = Strings.Current.Format("Install.NexusLink.Confirm.Content", DestinationName()),
                    PrimaryButtonText = Strings.Current["Install.NexusLink.Confirm.PrimaryButton"],
                    CloseButtonText = Strings.Current["Install.NexusLink.Confirm.CloseButton"],
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = root
                };

                try
                {
                    if (await DialogText.ShowAsync(dialog) != ContentDialogResult.Primary)
                    {
                        LoggingService.Log($"Nexus link {link.Redacted}: canceled at the destination confirmation.");
                        return;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    LoggingService.LogException(ex, "Could not confirm the destination for a Nexus link");
                }
            }
            else
            {
                LoggingService.Log(
                    $"Nexus link {link.Redacted}: no window was up to confirm the destination, so the download ran.");
            }

            await install.DownloadFromNexusAsync(link);
        }

        // The version by name, read from the instance registry rather than from a page, because a
        // link can arrive before any page has ever refreshed. With no instance registered the
        // destination is the ordinary machine install and there is no version to name.
        private static string DestinationName()
        {
            try
            {
                var active = new InstanceManager(CanonicalPathSet.ForMachine(), new InstanceSettingsStore()).Active();

                return active is null
                    ? Strings.Current["Install.NexusLink.Destination.Unnamed"]
                    : $"{active.Record.DisplayName} ('{active.Record.GameFolder}')";
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Could not name the destination version for a Nexus link");
                return Strings.Current["Install.NexusLink.Destination.Unnamed"];
            }
        }

        public static async Task<NxmDownloadResult> DownloadAsync(
            NxmLink link,
            IProgress<NxmDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            var transport = new NexusHttpTransport();
            var keyStore = new NexusApiKeyStore(NexusApiKeyStore.DefaultDirectory, new WindowsSecretProtector());

            var result = await new NxmDownload(new NexusClient(transport), transport).RunAsync(
                link,
                keyStore.Load(),
                ArchivesFolder(),
                DateTimeOffset.UtcNow,
                progress,
                cancellationToken);

            LastActivationMessage = result.Message;
            LoggingService.Log($"Nexus link {link.Redacted}: {result.Status}");

            return result;
        }

        // The folder BEM already watches, so a downloaded archive appears on the Install page the
        // same way one downloaded in a browser does. Read from BEM's settings file rather than from
        // the install page's own state, so this works whether or not that page has been opened.
        //
        // Null when none is set. There is deliberately no fallback folder: an archive dropped where
        // BEM does not look is, to the person who clicked the button, a download that never happened.
        public static string? ArchivesFolder()
        {
            var settingsPath = Path.Combine(NexusApiKeyStore.DefaultDirectory, "settings.json");

            try
            {
                if (File.Exists(settingsPath)
                    && JsonDocument.Parse(File.ReadAllText(settingsPath)).RootElement
                        .TryGetProperty("ArchivesFolderPath", out var folder)
                    && folder.GetString() is { Length: > 0 } path
                    && Directory.Exists(path))
                    return path;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }

            return null;
        }

        private static async Task AnnounceAsync(Window window, string message, string? title = null)
        {
            if ((window.Content as FrameworkElement)?.XamlRoot is not { } root)
                return;

            try
            {
                await DialogText.ShowAsync(new ContentDialog
                {
                    Title = title ?? Strings.Current["Install.NexusLink.Announce.Title"],
                    Content = message,
                    CloseButtonText = Strings.Current["Install.NexusLink.Announce.CloseButton"],
                    XamlRoot = root
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                LoggingService.LogException(ex, "Failed to show the Nexus link notice");
            }
        }
    }
}
