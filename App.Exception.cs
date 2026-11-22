using BannerlordEnvironmentManager.Core.DryRun;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager
{
    // The Bannerlord Exception Manager's start path: `--exception "<record>"`, given by the Companion from
    // inside a game that has just caught a crash and is holding its frame for the answer.
    public partial class App
    {
        public const string ExceptionSwitch = "--exception";

        // True when this start was for a crash record, whether or not a window could be shown: such a
        // start must never go on to become a second BEM, or hand itself to a running one, which would
        // take the argument and leave the game waiting on a window nobody opened. None of the shell's
        // startup work runs either. The game is alive behind this, and the junction repair in
        // particular must not look at an instance that is in the middle of being played.
        private bool TryShowExceptionWindow(string[] args)
        {
            var at = Array.FindIndex(args, a => string.Equals(a, ExceptionSwitch, StringComparison.OrdinalIgnoreCase));

            if (at < 0)
                return false;

            var recordPath = at + 1 < args.Length ? args[at + 1] : string.Empty;

            try
            {
                UseTheRecordsDataRoot(recordPath);
                StartLocalizationAtStartup();
                UnhandledException += App_UnhandledException;

                // Before anything of ours is on screen: the picture is of the game, not of this window.
                var gameFrame = GameScreenshot.Grab(ExceptionReport.ProcessIdOf(CrashGuardRecord.Read(recordPath)?.RunId));

                var window = new Window();
                AppWindow = window;
                window.AppWindow.TitleBar.PreferredTheme = Microsoft.UI.Windowing.TitleBarTheme.Dark;
                // The version, as in the main window's title: a screenshot of a crash then says which BEM read it.
                window.Title = $"{Strings.Current["Exception.Title"]} {AppVersion.Display} by Alkeari";

                // The game is the foreground window, often borderless across the whole screen, and it
                // is not answering Windows while it waits. Above it is the only place this is seen.
                if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                    presenter.IsAlwaysOnTop = true;

                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));

                var viewModel = new ExceptionWindowViewModel(recordPath) { GameFrame = gameFrame };
                var frame = new Frame();

                window.Content = frame;

                // Closing ends the process, which is what tells the Companion nobody will answer.
                viewModel.Answered += window.Close;
                window.Closed += (_, _) => Exit();

                window.Activate();
                BringToFront(WinRT.Interop.WindowNative.GetWindowHandle(window));
                _ = frame.Navigate(typeof(ExceptionPage), viewModel);
                SetWindowIcon(window);

                LoggingService.Log($"Bannerlord Exception Manager opened for {recordPath}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                LoggingService.LogException(ex, "Failed to open the Bannerlord Exception Manager");
                Exit();
            }

            return true;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        // Every window opens restored and in front of the game. A second window of a session started minimized
        // behind it: Windows keeps the foreground with the game, and a window the system placed from the
        // first one's state can come up iconic. Restore, join the foreground thread's input so the foreground
        // call is honoured, and pulse topmost; the Companion also grants this process the foreground.
        private static void BringToFront(IntPtr handle)
        {
            try
            {
                const int SwRestore = 9;
                const uint NoMoveNoSize = 0x0003, ShowWindowFlag = 0x0040;
                var topmost = new IntPtr(-1);
                var foreground = GetForegroundWindow();
                var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
                var thisThread = GetCurrentThreadId();
                var attached = foregroundThread != 0 && foregroundThread != thisThread && AttachThreadInput(thisThread, foregroundThread, true);

                ShowWindow(handle, SwRestore);
                SetWindowPos(handle, topmost, 0, 0, 0, 0, NoMoveNoSize | ShowWindowFlag);
                SetForegroundWindow(handle);

                if (attached)
                    AttachThreadInput(thisThread, foregroundThread, false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LoggingService.LogException(ex, "Could not bring the Bannerlord Exception Manager to the front");
            }
        }

        // The record sits in <data root>\crash-guard, so its own location says which data root the BEM
        // that launched the game was on. The Companion passes no switch for it: a driven run on a root
        // of its own is the only case, and the path already carries the answer.
        private static void UseTheRecordsDataRoot(string recordPath)
        {
            try
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(recordPath));
                var root = Path.GetDirectoryName(folder);

                // Only a record that sits where the Companion writes them names a data root; a
                // record opened from anywhere else is read on the root this run already has.
                if (folder is null || root is null || !Directory.Exists(root)
                    || !string.Equals(Path.GetFileName(folder), Path.GetFileName(CrashGuardRecord.DefaultFolder), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(root, Path.GetFullPath(AppDataRoot.Folder), StringComparison.OrdinalIgnoreCase))
                    return;

                AppDataRoot.Use(root);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
            {
                LoggingService.LogException(ex, "Could not take the data root from the crash record's path");
            }
        }
    }
}
