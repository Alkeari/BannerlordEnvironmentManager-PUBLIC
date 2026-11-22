using BannerlordEnvironmentManager.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace BannerlordEnvironmentManager.Controls;

// One setting: its label on the left and its control on the right (design system 7.2). What the
// setting does is the label's tooltip, never a printed line. A hairline separates it from the row
// above, except for the first row in its panel. Built in code because it is only a two-column grid.
[ContentProperty(Name = nameof(Control))]
public sealed partial class SettingRow : Grid
{
    public static readonly DependencyProperty LabelKeyProperty = DependencyProperty.Register(
        nameof(LabelKey), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty TooltipKeyProperty = DependencyProperty.Register(
        nameof(TooltipKey), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty, OnChanged));

    // A tooltip that is state rather than a catalog string (a note the view model writes).
    public static readonly DependencyProperty TooltipProperty = DependencyProperty.Register(
        nameof(Tooltip), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty ControlProperty = DependencyProperty.Register(
        nameof(Control), typeof(UIElement), typeof(SettingRow), new PropertyMetadata(null, OnControlChanged));

    private readonly TextBlock label = new();
    private readonly ContentPresenter host = new() { VerticalAlignment = VerticalAlignment.Center };

    public SettingRow()
    {
        ColumnSpacing = 20;
        Padding = new Thickness(0, 10, 0, 10);
        MinHeight = 52;
        BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AlkHairlineBrush"];

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        label.Style = (Style)Application.Current.Resources["SettingLabelText"];
        SetColumn(host, 1);

        Children.Add(label);
        Children.Add(host);

        Loaded += (_, _) => ApplySeparator();
    }

    public string LabelKey
    {
        get => (string)GetValue(LabelKeyProperty);
        set => SetValue(LabelKeyProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string TooltipKey
    {
        get => (string)GetValue(TooltipKeyProperty);
        set => SetValue(TooltipKeyProperty, value);
    }

    public string Tooltip
    {
        get => (string)GetValue(TooltipProperty);
        set => SetValue(TooltipProperty, value);
    }

    public UIElement? Control
    {
        get => (UIElement?)GetValue(ControlProperty);
        set => SetValue(ControlProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        _ = e;
        ((SettingRow)d).Apply();
    }

    private static void OnControlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (SettingRow)d;
        row.host.Content = e.NewValue;
    }

    private void Apply()
    {
        label.Text = string.IsNullOrEmpty(LabelKey) ? Label : Strings.Current[LabelKey];

        var tip = !string.IsNullOrEmpty(Tooltip)
            ? Tooltip
            : string.IsNullOrEmpty(TooltipKey) ? label.Text : Strings.Current[TooltipKey];

        ToolTipService.SetToolTip(label, string.IsNullOrEmpty(tip) ? null : tip);
    }

    private void ApplySeparator()
    {
        var first = Parent is Panel panel
            && panel.Children.OfType<FrameworkElement>().FirstOrDefault(child => child.Visibility == Visibility.Visible) == this;

        BorderThickness = first ? new Thickness(0) : new Thickness(0, 1, 0, 0);
    }
}
