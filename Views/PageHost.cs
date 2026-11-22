using Microsoft.UI.Xaml.Navigation;

namespace BannerlordEnvironmentManager.Views
{
    // One page with views beats many pages with purposes (design system 7.17, redesign proposal 1.1): the
    // pages that used to be navigation targets of their own (the four Health checks and the Validator,
    // the three Settings tools, the four Forensics evidence pages) are now shown inside the page that
    // owns them, in an embedded frame. This is the one map from such a page to its owner, used both
    // ways: a navigation to one of them anywhere in BEM lands on the owner with that view open, and a
    // navigation an embedded page makes to anything else (Show on Play) goes to the shell's frame.
    public static class PageHost
    {
        // The owning page and the view key it opens for each hosted page.
        public static (Type Owner, string View)? OwnerOf(Type? page) => page?.Name switch
        {
            nameof(InstallChecksPage) => (typeof(TroubleshootPage), "InstallChecks"),
            nameof(ModOverlapsPage) => (typeof(TroubleshootPage), "Overlaps"),
            nameof(ModSafetyPage) => (typeof(TroubleshootPage), "Safety"),
            nameof(SubModuleValidatorPage) => (typeof(TroubleshootPage), "Validator"),

            // The settings folders of every mod at once and the helper programs, Troubleshoot's last two tabs.
            nameof(ModSettingsPage) => (typeof(TroubleshootPage), "ModSettings"),
            nameof(ToolkitPage) => (typeof(TroubleshootPage), "Tools"),

            // The bisection has one way in: Troubleshoot's Find the Culprit tab.
            nameof(BootChecksPage) => (typeof(TroubleshootPage), "Culprit"),

            // The crash evidence is Troubleshoot's Crashes tab, in Basic and Advanced alike.
            nameof(DiagnosticsPage) => (typeof(TroubleshootPage), "Reports"),
            nameof(DiagnosticLogsPage) => (typeof(TroubleshootPage), "Logs"),
            nameof(DiagnosticFilesPage) => (typeof(TroubleshootPage), "Files"),
            nameof(WatchPage) => (typeof(TroubleshootPage), "Watch"),
            _ => null,
        };

        // Shows a hosted page in an owner's frame. The frame refuses every other navigation and hands it
        // to the shell instead, so an embedded page's own Frame.Navigate still reaches the page it named.
        public static void Show(Frame frame, Type page, object? parameter = null)
        {
            ArgumentNullException.ThrowIfNull(frame);

            if (frame.Tag is not Type)
                frame.Navigating += OnEmbeddedNavigating;

            frame.Tag = page;

            if (frame.CurrentSourcePageType != page)
                frame.Navigate(page, parameter);
        }

        private static void OnEmbeddedNavigating(object sender, NavigatingCancelEventArgs e)
        {
            if (sender is not Frame { Tag: Type allowed } || e.SourcePageType == allowed)
                return;

            e.Cancel = true;

            var target = e.SourcePageType;
            var parameter = e.Parameter;

            ShellPage.Open(target, parameter);
        }
    }
}
