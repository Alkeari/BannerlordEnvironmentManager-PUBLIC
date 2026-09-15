using BannerlordEnvironmentManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BannerlordEnvironmentManager.Views
{
    public sealed class ModuleRowTemplateSelector : DataTemplateSelector
    {
        public DataTemplate ModuleTemplate { get; set; } = null!;

        public DataTemplate DividerTemplate { get; set; } = null!;

        protected override DataTemplate SelectTemplateCore(object item) =>
            item is ILoadOrderRowViewModel { IsDivider: true } ? DividerTemplate : ModuleTemplate;
    }
}
