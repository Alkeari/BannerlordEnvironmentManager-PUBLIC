#if WINDOWSAPPSDK_SINGLEFILE
using System;
using System.Runtime.CompilerServices;

namespace BannerlordEnvironmentManager;

internal static class WindowsAppSdkSingleFileBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable(
            "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY",
            AppContext.BaseDirectory);
    }
}
#endif

