namespace BannerlordEnvironmentManager.Views
{
    // Advanced Mode splits the product into an everyday surface and everything else. Basic keeps the
    // whole core loop - install, order, launch, play, and see what broke when something breaks -
    // while the investigative and power-user surfaces (the Forensics destination, the sort-key and
    // pin machinery, the mod-id repair tools, the quarantine and restore panels, the launch tuning)
    // show only in Advanced.
    //
    // Two rules make Basic safe as the default. First, presentation only: nothing is disabled and
    // nothing stops running - scans still scan, backups still happen - Basic changes what is shown,
    // never what BEM does. Second, the safety floor: a section holding an active finding or a running
    // session is visible in both modes. Show() carries that floor for the finding-bearing sections;
    // Advanced() is for surfaces that hold no findings and may hide outright.
    //
    // Shared rather than repeated per page, because seven copies of the same three lines is seven
    // places for the rule to drift.
    public static class PageDensity
    {
        public static Visibility Show(bool advanced, bool hasContent) =>
            advanced || hasContent ? Visibility.Visible : Visibility.Collapsed;

        public static Visibility Show(bool advanced, string content) =>
            advanced || !string.IsNullOrEmpty(content) ? Visibility.Visible : Visibility.Collapsed;

        public static Visibility Show(bool advanced, int count) =>
            advanced || count > 0 ? Visibility.Visible : Visibility.Collapsed;

        public static Visibility Advanced(bool advanced) =>
            advanced ? Visibility.Visible : Visibility.Collapsed;

        // Something that exists only in Advanced Mode and, even there, only while it has something to show.
        public static Visibility AdvancedAnd(bool advanced, bool hasContent) =>
            advanced && hasContent ? Visibility.Visible : Visibility.Collapsed;

        // Something that exists only in Basic and, even there, only while it has something to show:
        // the simple stand-in for a fuller Advanced control.
        public static Visibility BasicAnd(bool advanced, bool hasContent) =>
            !advanced && hasContent ? Visibility.Visible : Visibility.Collapsed;

        // A table column that exists in Advanced Mode only: its share in Advanced, nothing in Basic,
        // so the header and every row drop it together.
        public static GridLength AdvancedColumn(bool advanced, double stars) =>
            advanced ? new GridLength(stars, GridUnitType.Star) : new GridLength(0);

        // For the one surface that exists only to stand in for a hidden advanced one: the Health
        // tile that reaches Crash Reports while the Forensics destination is out of the navigation.
        public static Visibility Basic(bool advanced) =>
            advanced ? Visibility.Collapsed : Visibility.Visible;
    }
}
