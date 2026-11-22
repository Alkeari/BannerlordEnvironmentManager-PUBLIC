using System.Runtime.InteropServices;

namespace BannerlordEnvironmentManager.Services
{
    /// <summary>
    /// A notification-area icon with a right-click menu, made with the shell's own API because WinUI 3
    /// has none. It owns a message-only window to receive the shell's callbacks, so it must be created
    /// and disposed on the UI thread, whose message loop delivers them.
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        private const uint CallbackMessage = 0x0400 + 1;
        private const uint WmLButtonUp = 0x0202;
        private const uint WmRButtonUp = 0x0205;
        private const uint WmContextMenu = 0x007B;
        private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
        private const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10;
        private const uint MfString = 0, TpmReturnCmd = 0x100, TpmRightButton = 2;
        private static readonly IntPtr HwndMessage = new(-3);

        private readonly string tooltip;
        private readonly IReadOnlyList<(int Id, string Text)> items;
        private readonly Action open;
        private readonly Action<int> chosen;
        private readonly WndProc wndProc;
        private readonly uint taskbarCreated;
        private readonly string className = "BemTrayWindow" + Guid.NewGuid().ToString("N");
        private IntPtr hwnd;
        private IntPtr icon;
        private bool added;

        public TrayIcon(string tooltip, IReadOnlyList<(int Id, string Text)> items, Action open, Action<int> chosen)
        {
            this.tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
            this.items = items;
            this.open = open;
            this.chosen = chosen;
            wndProc = Proc;
            taskbarCreated = RegisterWindowMessage("TaskbarCreated");

            var instance = GetModuleHandle(null);
            var cls = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
                hInstance = instance,
                lpszClassName = className
            };

            if (RegisterClassEx(ref cls) == 0)
                throw new InvalidOperationException("Could not register the tray window class.");

            hwnd = CreateWindowEx(0, className, string.Empty, 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("Could not create the tray window.");

            if (Environment.ProcessPath is { Length: > 0 } exe)
                icon = ExtractIcon(IntPtr.Zero, exe, 0);

            Add();
        }

        private void Add()
        {
            var data = Data();
            data.uFlags = NifMessage | NifIcon | NifTip;
            data.uCallbackMessage = CallbackMessage;
            data.hIcon = icon;
            data.szTip = tooltip;
            added = ShellNotifyIcon(NimAdd, ref data);
        }

        /// <summary>A balloon from the icon: the notification the shell shows beside the clock.</summary>
        public void Notify(string title, string text)
        {
            if (!added)
                return;

            var data = Data();
            data.uFlags = NifInfo;
            data.szInfoTitle = title.Length > 63 ? title[..63] : title;
            data.szInfo = text.Length > 255 ? text[..255] : text;
            data.dwInfoFlags = 1;
            ShellNotifyIcon(NimModify, ref data);
        }

        private NotifyIconData Data() => new()
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = hwnd,
            uID = 1
        };

        private IntPtr Proc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == CallbackMessage)
            {
                var mouse = (uint)(lParam.ToInt64() & 0xFFFF);

                if (mouse == WmLButtonUp)
                    open();
                else if (mouse == WmRButtonUp || mouse == WmContextMenu)
                    ShowMenu();

                return IntPtr.Zero;
            }

            if (message == taskbarCreated && taskbarCreated != 0)
            {
                Add();
                return IntPtr.Zero;
            }

            return DefWindowProc(window, message, wParam, lParam);
        }

        private void ShowMenu()
        {
            var menu = CreatePopupMenu();

            try
            {
                foreach (var (id, text) in items)
                    AppendMenu(menu, MfString, (UIntPtr)(uint)id, text);

                GetCursorPos(out var point);
                SetForegroundWindow(hwnd);
                var command = TrackPopupMenu(menu, TpmReturnCmd | TpmRightButton, point.X, point.Y, 0, hwnd, IntPtr.Zero);
                PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero);

                if (command > 0)
                    chosen(command);
            }
            finally
            {
                DestroyMenu(menu);
            }
        }

        public void Dispose()
        {
            if (added)
            {
                var data = Data();
                ShellNotifyIcon(NimDelete, ref data);
                added = false;
            }

            if (hwnd != IntPtr.Zero)
            {
                DestroyWindow(hwnd);
                hwnd = IntPtr.Zero;
                UnregisterClass(className, GetModuleHandle(null));
            }

            if (icon != IntPtr.Zero)
            {
                DestroyIcon(icon);
                icon = IntPtr.Zero;
            }
        }

        private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClassEx
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ExtractIcon(IntPtr instance, string file, int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WndClassEx cls);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterClass(string className, IntPtr instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? name);
    }
}
