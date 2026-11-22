using System.Runtime.InteropServices.WindowsRuntime;
using BannerlordEnvironmentManager.Core.Instances;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BannerlordEnvironmentManager.Services
{
    // Paints Steam's sign-in QR code from the module grid DepotDownloader printed as text.
    //
    // Black on white, always, whatever the app's theme: a camera reads a QR code by contrast, and the
    // palette's dark surface behind dark modules is a code that will not scan. The quiet zone is
    // already part of the grid QRCoder drew, so nothing is added around it here.
    public static class QrCodeImage
    {
        private const int PixelsPerModule = 8;

        public static WriteableBitmap? From(string asciiBlock)
        {
            if (AsciiQrCode.Parse(asciiBlock) is not { } matrix)
                return null;

            var modules = matrix.GetLength(0);
            var side = modules * PixelsPerModule;
            var bitmap = new WriteableBitmap(side, side);
            var pixels = new byte[side * side * 4];

            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++)
                {
                    var dark = matrix[y / PixelsPerModule, x / PixelsPerModule];
                    var value = dark ? (byte)0 : (byte)255;
                    var offset = ((y * side) + x) * 4;

                    // BGRA, and opaque: a translucent code would take the surface behind it as its
                    // background and stop scanning.
                    pixels[offset] = value;
                    pixels[offset + 1] = value;
                    pixels[offset + 2] = value;
                    pixels[offset + 3] = 255;
                }
            }

            using (var stream = bitmap.PixelBuffer.AsStream())
                stream.Write(pixels, 0, pixels.Length);

            bitmap.Invalidate();
            return bitmap;
        }
    }
}
