using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace BannerlordEnvironmentManager.Controls;

// A designed empty state. Three variants, told apart by color and wording, so a user can tell
// "found nothing" from "could not look" at a glance instead of staring at a bordered void.
public enum EmptyStateKind
{
    // Nothing to show, and that is good. Olive-gold check.
    Clear,

    // Nothing yet; press this. Steel gray, carries an action.
    Empty,

    // Could not look. Amber, carries the reason and the fix.
    CannotLook,
}

[ContentProperty(Name = nameof(Action))]
public sealed partial class EmptyState : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty ReasonProperty = DependencyProperty.Register(
        nameof(Reason), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(EmptyStateKind), typeof(EmptyState),
        new PropertyMetadata(EmptyStateKind.Empty, OnChanged));

    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
        nameof(Action), typeof(object), typeof(EmptyState), new PropertyMetadata(null, OnChanged));

    public EmptyState()
    {
        InitializeComponent();
        Apply();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Reason
    {
        get => (string)GetValue(ReasonProperty);
        set => SetValue(ReasonProperty, value);
    }

    public EmptyStateKind Kind
    {
        get => (EmptyStateKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public object? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((EmptyState)d).Apply();

    private void Apply()
    {
        // The title is trimmed rather than wrapped, so the whole of it has to be reachable on hover.
        TitleText.Text = Title ?? string.Empty;
        ToolTipService.SetToolTip(TitleText, string.IsNullOrWhiteSpace(Title) ? null : Title);

        ReasonText.Text = Reason ?? string.Empty;
        ReasonText.Visibility = string.IsNullOrWhiteSpace(Reason) ? Visibility.Collapsed : Visibility.Visible;
        ActionHost.Visibility = Action is null ? Visibility.Collapsed : Visibility.Visible;

        switch (Kind)
        {
            case EmptyStateKind.Clear:
                KindIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AlkSuccessOliveGoldBrush"];
                KindIcon.Glyph = "\uE73E";
                break;
            case EmptyStateKind.Empty:
                KindIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AlkSteelGrayBrush"];
                KindIcon.Glyph = "\uE710";
                break;
            case EmptyStateKind.CannotLook:
                KindIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AlkAlertAmberBrush"];
                KindIcon.Glyph = "\uE7BA";
                break;
        }
    }
}
