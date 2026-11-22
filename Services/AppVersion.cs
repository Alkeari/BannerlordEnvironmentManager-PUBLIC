using System.Reflection;

namespace BannerlordEnvironmentManager.Services
{
    // The one place BEM's own version is read, so the title bar, Settings and any log line all agree.
    // AssemblyInformationalVersion carries "<version>+<commit>"; the display form drops the commit,
    // which is build provenance rather than something a user reads back in a bug report.
    public static class AppVersion
    {
        public static string Display { get; } = Read();

        private static string Read()
        {
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (string.IsNullOrWhiteSpace(informational))
                return "";

            var plus = informational.IndexOf('+');

            return plus > 0 ? informational[..plus] : informational;
        }
    }
}
