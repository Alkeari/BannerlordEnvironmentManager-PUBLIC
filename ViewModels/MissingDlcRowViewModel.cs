using System.Windows.Input;
using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.ViewModels
{
    // One button in the Versions page's "add a DLC" row. The DataTemplate that renders it is
    // compiled against this type rather than GameDlcInfo directly, because a DataTemplate cannot
    // reach the page's own view model for the command to run: the command comes along with the
    // item instead.
    public sealed class MissingDlcRowViewModel(GameDlcInfo dlc, ICommand addCommand)
    {
        public GameDlcInfo Dlc { get; } = dlc;

        public ICommand AddCommand { get; } = addCommand;

        public string AddLabel => Strings.Current.Format("Versions.AddDlcButton", Dlc.DisplayName);

        public string AddTooltip => Strings.Current.Format("Versions.AddDlcButton.Tooltip", Dlc.DisplayName);
    }
}
