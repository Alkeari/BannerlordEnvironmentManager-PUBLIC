using BannerlordEnvironmentManager.Core.Install;

namespace BannerlordEnvironmentManager.Services
{
    /// <summary>
    /// The steps that run before every game launch, whoever starts the game: BEM's own Launch and the tray
    /// watcher both go through here, so the two cannot drift apart. The steps themselves are Core's
    /// <see cref="PreLaunchSteps"/>; this only supplies the Windows file unblocker and the log.
    /// </summary>
    internal static class PreLaunchRunner
    {
        public static Task<PreLaunchResult> RunAsync(string gameInstallPath) => Task.Run(() =>
        {
            try
            {
                var result = PreLaunchSteps.Run(
                    gameInstallPath,
                    file => FileUnblocker.UnblockFile(file).Status == FileUnblocker.UnblockStatus.Unblocked,
                    new QuarantineStore(QuarantineStore.DefaultRoot));

                if (result.Unblocked > 0)
                    LoggingService.Log($"Unblocked {result.Unblocked} downloaded mod file(s) before launch.");

                if (result.ShaderFoldersSetAside > 0)
                    LoggingService.Log($"Set aside {result.ShaderFoldersSetAside} mod shader folder(s) before launch.");

                if (result.PlatformFoldersAdopted > 0)
                    LoggingService.Log($"Copied {result.PlatformFoldersAdopted} other-platform binary folder(s) for this install's platform before launch.");

                if (result.PlatformFoldersSetAside > 0)
                    LoggingService.Log($"Set aside {result.PlatformFoldersSetAside} other-platform binary folder(s) before launch.");

                foreach (var failure in result.Failures)
                    LoggingService.Log(failure);

                return result;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogException(ex, "Preparing the game folder before launch failed");
                return PreLaunchResult.Nothing;
            }
        });
    }
}
