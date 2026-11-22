using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BannerlordEnvironmentManager.Services
{
    // Every "Browse folder" in BEM goes through here. The old Windows.Storage picker throws for an
    // app run as administrator (and on some machines for an unpackaged app), and a throw inside an
    // async command ended BEM: a tester's Settings > Archives Folder > Browse Folder on 2026-11-13.
    // The Windows App SDK picker works elevated, the old one is the fallback, and a failure of both
    // comes back as a message, never an exception.
    public static class FolderPick
    {
        public sealed record Result(string? Path, string? Error);

        public static async Task<Result> PickAsync()
        {
            var window = App.AppWindow;

            if (window is null)
                return new Result(null, "BEM's window is not ready yet.");

            try
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(window.AppWindow.Id);
                var picked = await picker.PickSingleFolderAsync();
                return new Result(picked?.Path, null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LoggingService.LogException(ex, "The Windows App SDK folder picker failed; trying the older one");
            }

            try
            {
                var picker = new FolderPicker();
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
                picker.FileTypeFilter.Add("*");
                var folder = await picker.PickSingleFolderAsync();
                return new Result(folder?.Path, null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LoggingService.LogException(ex, "The folder picker failed");
                return new Result(null, ex.Message);
            }
        }
    }
}
