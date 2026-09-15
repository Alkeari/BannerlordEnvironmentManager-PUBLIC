using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Toolkit;

// The closed set of programs a BEM capability is actually gated on and that BEM cannot ship, arrived at
// by reading every place BEM starts a process. The bar is deliberately high: a program BEM merely
// prefers is not in here, because a row offering something BEM does not need is clutter on a product
// whose user already cannot find their own pages.
//
// Deliberately excluded, with the reason, so the next reader does not have to work it out again:
//   ILSpy       BEM already decompiles in process through the ICSharpCode.Decompiler package it ships
//               and MethodDecompiler already calls. An entry would change nothing.
//   Notepad++   BEM probes for it when opening SubModule.xml and log files, but opens them through the
//               shell when it is absent. Nothing is gated on it, so nothing is offered for it.
//   Vortex, MO2 BEM forwards an nxm link to whichever program held the registration before it. It
//               needs none of them installed and gains nothing from installing one.
//   reg.exe, powershell.exe, explorer.exe   Windows' own, not third party.
public static class ToolkitCatalog
{
    public const string SevenZipId = "7zip";

#if DEV_BEM
    public const string DotPeekId = "dotpeek";
#endif

    public static IReadOnlyList<ExternalTool> Tools { get; } =
    [
        new ExternalTool(
            SevenZipId,
            "7-Zip",
            Strings.Current["Core.Toolkit.Catalog.SevenZip.Capability"],
            Strings.Current["Core.Toolkit.Catalog.SevenZip.WithoutIt"],
            "7zip.7zip",
            null,
            "https://www.7-zip.org/"),

#if DEV_BEM
        // The outer ring. A decompiler is a tool for reading someone else's assembly, which is work
        // the author does and a player never asks for; the crash report it feeds is Dev-BEM's own.
        //
        // JetBrains ships dotPeek as an interactive dotUltimate web installer that lands outside the
        // folder BEM reads, so an install started here could not be confirmed afterwards and the crash
        // report button would stay dark over a working install. The link is offered instead of a lie.
        new ExternalTool(
            DotPeekId,
            "dotPeek",
            Strings.Current["Core.Toolkit.Catalog.DotPeek.Capability"],
            Strings.Current["Core.Toolkit.Catalog.DotPeek.WithoutIt"],
            null,
            Strings.Current["Core.Toolkit.Catalog.DotPeek.NotInstallableReason"],
            "https://www.jetbrains.com/decompiler/"),
#endif
    ];

    public static ExternalTool? ById(string id) =>
        Tools.FirstOrDefault(tool => string.Equals(tool.Id, id, StringComparison.Ordinal));

    // The same probe the consuming feature uses, so this can never say "installed" over a capability
    // that stays switched off.
    public static string? Locate(string id) => id switch
    {
        SevenZipId => SevenZipLocator.Locate(),
#if DEV_BEM
        DotPeekId => DotPeekLocator.Locate(),
#endif
        _ => null
    };
}
