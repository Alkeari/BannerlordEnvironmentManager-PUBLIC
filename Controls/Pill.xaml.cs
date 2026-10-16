using BannerlordEnvironmentManager.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BannerlordEnvironmentManager.Controls;

// How a row mark reads. Amber is something to do, olive-gold is checked and clear, steel gray is
// identity and never competes for the eye.
public enum PillKind
{
    Identity,
    Clear,
    Action,
}

public sealed partial class Pill : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Pill), new PropertyMetadata(string.Empty, OnChanged));

    // TextKey exists for the same reason PageHeader's TitleKey does: Pill is a UserControl, not a
    // ContentControl, so none of Loc's attached properties can resolve it. Text keeps meaning literal
    // display text; TextKey is the migrated form, and wins when set and non-empty.
    public static readonly DependencyProperty TextKeyProperty = DependencyProperty.Register(
        nameof(TextKey), typeof(string), typeof(Pill), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(PillKind), typeof(Pill), new PropertyMetadata(PillKind.Identity, OnChanged));

    public Pill()
    {
        InitializeComponent();
        Apply();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string TextKey
    {
        get => (string)GetValue(TextKeyProperty);
        set => SetValue(TextKeyProperty, value);
    }

    public PillKind Kind
    {
        get => (PillKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((Pill)d).Apply();

    private void Apply()
    {
        PillText.Text = !string.IsNullOrEmpty(TextKey) ? Strings.Current[TextKey] : Text ?? string.Empty;

        var resources = Application.Current.Resources;
        var brush = Kind switch
        {
            PillKind.Clear => (Microsoft.UI.Xaml.Media.Brush)resources["AlkSuccessOliveGoldBrush"],
            PillKind.Action => (Microsoft.UI.Xaml.Media.Brush)resources["AlkAlertAmberBrush"],
            _ => (Microsoft.UI.Xaml.Media.Brush)resources["AlkSteelGrayBrush"],
        };

        // The kind tints the outline and the label; the fill stays the raised surface the Mark style
        // gives it. Painting the background with the same brush as the text made the word invisible:
        // the Versions page showed two solid gold lozenges where "Resting" and "Active" should read.
        PillBorder.BorderBrush = brush;
        PillText.Foreground = brush;
    }
}