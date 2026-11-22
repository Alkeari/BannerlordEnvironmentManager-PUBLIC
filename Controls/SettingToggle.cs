using BannerlordEnvironmentManager.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace BannerlordEnvironmentManager.Controls;

// The design system's toggle (section 5): ON is a Forged Gold box with a black caption, OFF the raised
// fill with a Steel Gray caption, and the caption always says which ("On", "Off"), so the state never
// rests on color alone. A ToggleButton underneath, so IsChecked binds exactly as a CheckBox's did.
public sealed partial class SettingToggle : ToggleButton
{
    public SettingToggle()
    {
        Style = (Style)Application.Current.Resources["SettingToggleStyle"];

        // Lightweight styling scoped to this control alone, so the other toggle buttons keep theirs.
        Map("ToggleButtonBackground", "AlkRaisedBrush");
        Map("ToggleButtonBackgroundPointerOver", "AlkRaisedHoverBrush");
        Map("ToggleButtonBackgroundPressed", "AlkRaisedHoverBrush");
        Map("ToggleButtonForeground", "AlkSteelGrayBrush");
        Map("ToggleButtonForegroundPointerOver", "AlkSoftChampagneBrush");
        Map("ToggleButtonForegroundPressed", "AlkSoftChampagneBrush");
        Map("ToggleButtonBorderBrush", "AlkHairlineBrush");
        Map("ToggleButtonBorderBrushPointerOver", "AlkHairlineBrush");
        Map("ToggleButtonBorderBrushPressed", "AlkHairlineBrush");
        Map("ToggleButtonBackgroundChecked", "AlkPrimaryBrush");
        Map("ToggleButtonBackgroundCheckedPointerOver", "AlkWarmGoldShadowBrush");
        Map("ToggleButtonBackgroundCheckedPressed", "AlkWarmGoldShadowBrush");
        Map("ToggleButtonForegroundChecked", "AlkBackgroundBrush");
        Map("ToggleButtonForegroundCheckedPointerOver", "AlkBackgroundBrush");
        Map("ToggleButtonForegroundCheckedPressed", "AlkBackgroundBrush");
        Map("ToggleButtonBorderBrushChecked", "AlkWarmGoldShadowBrush");
        Map("ToggleButtonBorderBrushCheckedPointerOver", "AlkWarmGoldShadowBrush");
        Map("ToggleButtonBorderBrushCheckedPressed", "AlkWarmGoldShadowBrush");

        Checked += (_, _) => ApplyCaption();
        Unchecked += (_, _) => ApplyCaption();
        Indeterminate += (_, _) => ApplyCaption();
        Loaded += (_, _) => ApplyCaption();

        ApplyCaption();
    }

    private void Map(string key, string brushKey) => Resources[key] = Application.Current.Resources[brushKey];

    private void ApplyCaption()
    {
        var on = IsChecked == true;

        Content = Strings.Current[on ? "Controls.SettingToggle.On" : "Controls.SettingToggle.Off"];
        FontWeight = on ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
    }
}
