using System.Globalization;
using System.Management;
using System.Runtime.Versioning;

namespace BannerlordEnvironmentManager.Services
{
    internal sealed record GpuAdapter(string Name, string DriverVersion, DateTime? DriverDate);

    internal sealed record GpuReport(IReadOnlyList<GpuAdapter> Adapters, string? Error);

    // Report only. BEM ships no list of known-bad drivers: any list short enough to maintain by hand
    // goes stale within a driver release or two, and a stale one accuses a driver that is fine.
    [SupportedOSPlatform("windows")]
    internal static class GpuInfo
    {
        public static GpuReport Read()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController");

                var adapters = new List<GpuAdapter>();

                foreach (var result in searcher.Get())
                {
                    using var adapter = result;

                    adapters.Add(new GpuAdapter(
                        Text(adapter, "Name"),
                        Text(adapter, "DriverVersion"),
                        ParseDate(Text(adapter, "DriverDate"))));
                }

                return new GpuReport(adapters, null);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "Reading the display adapters through WMI failed");
                return new GpuReport([], ex.Message);
            }
        }

        private static string Text(ManagementBaseObject adapter, string property)
        {
            try
            {
                return adapter[property]?.ToString() ?? string.Empty;
            }
            catch (ManagementException)
            {
                return string.Empty;
            }
        }

        // WMI hands back a CIM datetime, yyyyMMddHHmmss followed by fractional seconds and a UTC
        // offset. Only the date half is meaningful for a driver, and a value BEM cannot read is worth
        // less than saying nothing about the date at all.
        private static DateTime? ParseDate(string value) =>
            value.Length >= 8
            && DateTime.TryParseExact(
                value[..8],
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
                ? parsed
                : null;
    }
}
