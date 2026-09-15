using BannerlordEnvironmentManager.Core.Nexus;
using Microsoft.Win32;

namespace BannerlordEnvironmentManager.Services
{
    // The only place in BEM that touches the registry for the nxm scheme.
    //
    // Writes go to HKEY_CURRENT_USER\Software\Classes, which needs no elevation and which Windows
    // overlays on top of the machine-wide classes, so a per-user registration wins. Reads come from
    // the merged HKEY_CLASSES_ROOT view instead, because the handler being displaced may be a
    // machine-wide one and forwarding has to point at whatever would really have run.
    public sealed class WindowsNxmRegistry : INxmRegistry
    {
        private const string UserClassesPath = @"Software\Classes\" + NxmHandler.Scheme;

        private const string CommandSubKey = @"shell\open\command";

        private const string UrlProtocolValueName = "URL Protocol";

        // Names BEM as the owner of a registration whatever the exe is called, so a renamed or moved
        // BEM is never mistaken for a third-party handler and saved as something to forward to.
        private const string OwnerValueName = "BannerlordEnvironmentManagerHandler";

        public ProtocolHandlerEntry Read()
        {
            using var merged = Registry.ClassesRoot.OpenSubKey(NxmHandler.Scheme);
            using var command = merged?.OpenSubKey(CommandSubKey);
            using var mine = Registry.CurrentUser.OpenSubKey(UserClassesPath);

            return new ProtocolHandlerEntry(
                command?.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                merged?.GetValue(null) as string,
                mine?.GetValue(OwnerValueName) as string == "1");
        }

        public void WriteHandler(string command, string defaultValue, bool markAsBem)
        {
            using var scheme = Registry.CurrentUser.CreateSubKey(UserClassesPath, writable: true);

            scheme.SetValue(null, defaultValue, RegistryValueKind.String);
            scheme.SetValue(UrlProtocolValueName, string.Empty, RegistryValueKind.String);

            if (markAsBem)
                scheme.SetValue(OwnerValueName, "1", RegistryValueKind.String);
            else
                scheme.DeleteValue(OwnerValueName, throwOnMissingValue: false);

            using var open = scheme.CreateSubKey(CommandSubKey, writable: true);
            open.SetValue(null, command, RegistryValueKind.String);
        }

        public void DeleteHandler()
        {
            using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);

            classes?.DeleteSubKeyTree(NxmHandler.Scheme, throwOnMissingSubKey: false);
        }
    }
}
