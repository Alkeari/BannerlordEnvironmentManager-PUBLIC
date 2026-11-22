using BannerlordEnvironmentManager.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class BugReportPanel : UserControl
    {
        public BugReportPanel() => InitializeComponent();

        public DiagnosticsViewModel Diagnostics => ShellViewModels.Instance.Diagnostics;

        // Opening the panel to a list of nothing and a grayed out Draft button reads as a broken
        // feature. Reading the load order costs nothing and only happens once the panel is opened.
        private void OnExpanding(Expander sender, ExpanderExpandingEventArgs args)
        {
            if (Diagnostics.BugReportSubjects.Count == 0)
                Diagnostics.ListBugReportSubjectsCommand.Execute(null);
        }
    }
}
