using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace BannerlordEnvironmentManager.Services
{
    // The game's window as it stood when it failed, for the Bannerlord Exception Manager. The game holds
    // its frame while it waits for the answer, so what is on screen is the moment of the crash. It is
    // copied from the screen, inside the game window's bounds only, before the Exception Manager's own
    // window opens over it. Nothing is asked of the game itself, which is not answering Windows.
    public sealed record GameFrame(byte[] Bgra, int Width, int Height);

    public static partial class GameScreenshot
    {
        private const int SrcCopy = 0x00CC0020;

        private const int ExtendedFrameBounds = 9;

        private const uint RenderFullContent = 2;

        // Null whenever there is nothing honest to show: no such process, no window, a minimised window.
        // Each of those is logged, since a missing picture with no reason cannot be put right.
        public static GameFrame? Grab(int processId)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                var window = process.MainWindowHandle;

                // The process's main window is asked of Windows by a rule that can answer nothing for a
                // game that is not pumping messages, so its largest visible window is the fallback.
                if (window == IntPtr.Zero)
                    window = LargestVisibleWindow((uint)processId);

                if (window == IntPtr.Zero)
                    return Missing($"process {processId} has no visible window");

                if (IsIconic(window))
                    return Missing("the game's window is minimised");

                if (DwmGetWindowAttribute(window, ExtendedFrameBounds, out var bounds, Marshal.SizeOf<Rect>()) != 0
                    && !GetWindowRect(window, out bounds))
                    return Missing("the game's window gave no bounds");

                var width = bounds.Right - bounds.Left;
                var height = bounds.Bottom - bounds.Top;

                if (width < 16 || height < 16 || width > 16384 || height > 16384)
                    return Missing($"the game's window is {width} by {height}");

                return Copy(window, bounds.Left, bounds.Top, width, height)
                    ?? Missing($"neither the screen nor the window could be copied ({width} by {height} at {bounds.Left},{bounds.Top})");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                           or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                LoggingService.LogException(ex, "Could not copy the game's window for the crash report");
                return null;
            }
        }

        private static GameFrame? Missing(string reason)
        {
            LoggingService.Log($"No picture of the game for the crash report: {reason}.", LogLevel.Warn);
            return null;
        }

        private static IntPtr LargestVisibleWindow(uint processId)
        {
            var best = IntPtr.Zero;
            long bestArea = 0;

            EnumWindows(
                (candidate, state) =>
                {
                    GetWindowThreadProcessId(candidate, out var owner);

                    if (owner == processId && IsWindowVisible(candidate) && GetWindowRect(candidate, out var rect))
                    {
                        var area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);

                        if (area > bestArea)
                        {
                            bestArea = area;
                            best = candidate;
                        }
                    }

                    return true;
                },
                IntPtr.Zero);

            return best;
        }

        public static async Task SaveAsync(GameFrame frame, string pngPath)
        {
            ArgumentNullException.ThrowIfNull(frame);

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)frame.Width, (uint)frame.Height, 96, 96, frame.Bgra);
            await encoder.FlushAsync();

            var bytes = new byte[stream.Size];

            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
            }

            await File.WriteAllBytesAsync(pngPath, bytes);
        }

        private static GameFrame? Copy(IntPtr window, int left, int top, int width, int height)
        {
            var screen = GetDC(IntPtr.Zero);

            if (screen == IntPtr.Zero)
                return null;

            var memory = IntPtr.Zero;
            var bitmap = IntPtr.Zero;

            try
            {
                memory = CreateCompatibleDC(screen);

                // Negative height: rows run top to bottom, which is the order the encoder takes them in.
                var info = new BitmapInfo
                {
                    Size = Marshal.SizeOf<BitmapInfo>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32
                };

                bitmap = CreateDIBSection(memory, ref info, 0, out var bits, IntPtr.Zero, 0);

                if (memory == IntPtr.Zero || bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                    return null;

                var previous = SelectObject(memory, bitmap);
                // From the screen first, which is what the player saw. When that is refused, the window's
                // own last composed frame is asked of the compositor, which needs nothing of the game.
                var copied = BitBlt(memory, 0, 0, width, height, screen, left, top, SrcCopy)
                    || PrintWindow(window, memory, RenderFullContent);

                _ = SelectObject(memory, previous);

                if (!copied)
                    return null;

                var pixels = new byte[width * height * 4];

                Marshal.Copy(bits, pixels, 0, pixels.Length);

                return new GameFrame(pixels, width, height);
            }
            finally
            {
                if (bitmap != IntPtr.Zero)
                    _ = DeleteObject(bitmap);

                if (memory != IntPtr.Zero)
                    _ = DeleteDC(memory);

                _ = ReleaseDC(IntPtr.Zero, screen);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        // BITMAPINFOHEADER alone: 32 bits per pixel carries no colour table.
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public int Size;
            public int Width;
            public int Height;
            public short Planes;
            public short BitCount;
            public int Compression;
            public int SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public int ClrUsed;
            public int ClrImportant;
        }

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetWindowRect(IntPtr window, out Rect rect);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsIconic(IntPtr window);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowVisible(IntPtr window);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        private delegate bool WindowVisitor(IntPtr window, IntPtr state);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(WindowVisitor visitor, IntPtr state);

        [LibraryImport("dwmapi.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr GetDC(IntPtr window);

        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int ReleaseDC(IntPtr window, IntPtr dc);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr CreateCompatibleDC(IntPtr dc);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr SelectObject(IntPtr dc, IntPtr value);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int operation);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeleteObject(IntPtr value);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeleteDC(IntPtr dc);
    }
}
