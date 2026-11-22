using System.Collections;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;

namespace BannerlordEnvironmentManager.Controls;

// One choice of a segmented control: a stable key the page switches on, and the caption shown.
public sealed partial class SegmentedItem : ObservableObject
{
    private string label = string.Empty;
    private string tooltip = string.Empty;

    public SegmentedItem()
    {
    }

    public SegmentedItem(string key, string label, string tooltip = "")
    {
        Key = key;
        this.label = label;
        this.tooltip = tooltip;
    }

    public string Key { get; set; } = string.Empty;

    // Observable so a count in the caption ("Install Checks 5") can change without rebuilding the row.
    public string Label
    {
        get => label;
        set => SetProperty(ref label, value);
    }

    public string Tooltip
    {
        get => tooltip;
        set => SetProperty(ref tooltip, value);
    }
}

// Chips: a horizontal row of 4 px pills, the chosen one Forged Gold with a black caption. List: a
// vertical list, the chosen item gold text on a 12 percent gold wash with a 2 px gold left edge, the
// way the sidebar marks its active item (the Settings group list).
public enum SegmentedMode
{
    Chips,
    List,
}

// A row of chips where exactly one is chosen: the redesign's filter and tab control (the Health
// mockup's "All 9 / Install Checks 5" row, the Settings group list at a narrow width). Built from
// ToggleButtons so it needs no template of its own; the chosen chip is Forged Gold.
public sealed partial class Segmented : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(Segmented), new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(
        nameof(SelectedKey), typeof(string), typeof(Segmented), new PropertyMetadata(string.Empty, OnSelectedKeyChanged));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(SegmentedMode), typeof(Segmented), new PropertyMetadata(SegmentedMode.Chips, OnModeChanged));

    private readonly List<(SegmentedItem Item, ToggleButton Chip)> chips = [];

    public Segmented()
    {
        InitializeComponent();
        ApplyMode();
    }

    public SegmentedMode Mode
    {
        get => (SegmentedMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        _ = e;
        var control = (Segmented)d;
        control.ApplyMode();
        control.Rebuild();
    }

    private void Map(string key, string brushKey) => Resources[key] = Application.Current.Resources[brushKey];

    // The panel the current mode lays its items out in.
    private Panel ChipPanel => Mode == SegmentedMode.List ? ListPanel : ChipWrap;

    private void ApplyMode()
    {
        var list = Mode == SegmentedMode.List;

        Panel from = list ? ChipWrap : ListPanel;
        var moving = from.Children.ToList();
        from.Children.Clear();

        foreach (var child in moving)
            ChipPanel.Children.Add(child);

        ChipWrap.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        ListPanel.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        Scroller.VerticalScrollMode = list ? ScrollMode.Enabled : ScrollMode.Disabled;
        Scroller.VerticalScrollBarVisibility = list ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;

        Map("ToggleButtonBackground", "AlkTransparentBrush");
        Map("ToggleButtonBackgroundPointerOver", list ? "AlkRaisedBrush" : "AlkRaisedHoverBrush");
        Map("ToggleButtonBackgroundPressed", "AlkRaisedBrush");
        Map("ToggleButtonForeground", "AlkSteelGrayBrush");
        Map("ToggleButtonForegroundPointerOver", "AlkSoftChampagneBrush");
        Map("ToggleButtonForegroundPressed", "AlkSoftChampagneBrush");
        Map("ToggleButtonBorderBrush", list ? "AlkTransparentBrush" : "AlkHairlineBrush");
        Map("ToggleButtonBorderBrushPointerOver", list ? "AlkTransparentBrush" : "AlkHairlineBrush");
        Map("ToggleButtonBorderBrushPressed", list ? "AlkTransparentBrush" : "AlkHairlineBrush");
        Map("ToggleButtonBackgroundChecked", list ? "AlkSelectedRowBrush" : "AlkPrimaryBrush");
        Map("ToggleButtonBackgroundCheckedPointerOver", list ? "AlkSelectedRowBrush" : "AlkWarmGoldShadowBrush");
        Map("ToggleButtonBackgroundCheckedPressed", list ? "AlkSelectedRowBrush" : "AlkWarmGoldShadowBrush");
        Map("ToggleButtonForegroundChecked", list ? "AlkPrimaryBrush" : "AlkBackgroundBrush");
        Map("ToggleButtonForegroundCheckedPointerOver", list ? "AlkPrimaryBrush" : "AlkBackgroundBrush");
        Map("ToggleButtonForegroundCheckedPressed", list ? "AlkPrimaryBrush" : "AlkBackgroundBrush");
        Map("ToggleButtonBorderBrushChecked", list ? "AlkPrimaryBrush" : "AlkWarmGoldShadowBrush");
        Map("ToggleButtonBorderBrushCheckedPointerOver", list ? "AlkPrimaryBrush" : "AlkWarmGoldShadowBrush");
        Map("ToggleButtonBorderBrushCheckedPressed", list ? "AlkPrimaryBrush" : "AlkWarmGoldShadowBrush");
    }

    public event TypedEventHandler<Segmented, string>? SelectionChanged;

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string SelectedKey
    {
        get => (string)GetValue(SelectedKeyProperty);
        set => SetValue(SelectedKeyProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (Segmented)d;

        if (e.OldValue is INotifyCollectionChanged oldCollection)
            oldCollection.CollectionChanged -= control.OnItemsChanged;

        if (e.NewValue is INotifyCollectionChanged newCollection)
            newCollection.CollectionChanged += control.OnItemsChanged;

        control.Rebuild();
    }

    private static void OnSelectedKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (Segmented)d;
        control.ApplySelection();
        control.SelectionChanged?.Invoke(control, e.NewValue as string ?? string.Empty);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        Rebuild();
    }

    private void Rebuild()
    {
        foreach (var (item, _) in chips)
            item.PropertyChanged -= OnItemPropertyChanged;

        chips.Clear();
        ChipPanel.Children.Clear();

        if (ItemsSource is null)
            return;

        foreach (var item in ItemsSource.OfType<SegmentedItem>())
        {
            var chip = new ToggleButton
            {
                Style = (Style)Application.Current.Resources[Mode == SegmentedMode.List ? "SegmentListItem" : "SegmentChip"],
                Content = item.Label,
                Tag = item.Key,
            };

            if (!string.IsNullOrEmpty(item.Tooltip))
                ToolTipService.SetToolTip(chip, item.Tooltip);

            chip.Click += OnChipClicked;
            item.PropertyChanged += OnItemPropertyChanged;
            chips.Add((item, chip));
            ChipPanel.Children.Add(chip);
        }

        ApplySelection();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        _ = e;

        foreach (var (item, chip) in chips)
        {
            if (!ReferenceEquals(item, sender))
                continue;

            chip.Content = item.Label;
            ToolTipService.SetToolTip(chip, string.IsNullOrEmpty(item.Tooltip) ? null : item.Tooltip);
        }
    }

    private void OnChipClicked(object sender, RoutedEventArgs e)
    {
        _ = e;

        if (sender is ToggleButton { Tag: string key })
        {
            // A chip is never turned off by clicking it again: one is always chosen.
            SelectedKey = key;
            ApplySelection();
        }
    }

    private void ApplySelection()
    {
        foreach (var (item, chip) in chips)
        {
            var on = item.Key == SelectedKey;
            chip.IsChecked = on;
            chip.FontWeight = on ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }
}
