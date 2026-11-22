using System;
using System.Runtime.InteropServices;
using System.Text;
using BannerlordEnvironmentManager.Core.Instances;
using Microsoft.Win32;

namespace BannerlordEnvironmentManager.Services
{
    // Reads the limit Windows applies to the Recycle Bin on the drive holding a path: the per-volume
    // MaxCapacity (in MB) and NukeOnDelete the Recycle Bin properties dialog writes. A volume that has
    // never had its bin configured has no key, and that answers Unknown rather than a guess.
    public static class RecycleBinCapacity
    {
        private const string VolumesKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumePathName(string fileName, StringBuilder volumePath, int length);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int length);

        public static RecycleBinLimit For(string path)
        {
            try
            {
                var mount = new StringBuilder(260);
                var volume = new StringBuilder(64);

                if (!GetVolumePathName(path, mount, mount.Capacity)
                    || !GetVolumeNameForVolumeMountPoint(mount.ToString(), volume, volume.Capacity))
                    return RecycleBinLimit.Unknown;

                // \\?\Volume{guid}\ is keyed by the bare {guid}.
                var name = volume.ToString();
                var guid = name.Substring(name.IndexOf('{'), name.IndexOf('}') - name.IndexOf('{') + 1);

                using var key = Registry.CurrentUser.OpenSubKey($@"{VolumesKey}\{guid}");

                if (key is null)
                    return RecycleBinLimit.Unknown;

                var nuke = key.GetValue("NukeOnDelete") is int n && n != 0;
                long? capacity = key.GetValue("MaxCapacity") is int mb ? mb * 1024L * 1024L : null;

                return new RecycleBinLimit(capacity, nuke);
            }
            catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException or UnauthorizedAccessException)
            {
                return RecycleBinLimit.Unknown;
            }
        }
    }
}
