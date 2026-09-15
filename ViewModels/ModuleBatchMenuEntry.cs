using System.Windows.Input;

namespace BannerlordEnvironmentManager.ViewModels
{
    // One item of the multi-select context menu, already labelled with the count the action will act
    // on. MenuFlyout has no ItemsSource, so EnvironmentPage builds the items from these as the menu
    // opens; the label, the tooltip and which actions appear at all are decided here.
    public sealed record ModuleBatchMenuEntry(string Label, string Tooltip, ICommand Command);
}
