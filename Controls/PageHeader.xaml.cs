using BannerlordEnvironmentManager.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace BannerlordEnvironmentManager.Controls;

[ContentProperty(Name = nameof(Actions))]
public sealed partial class PageHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty BlurbProperty = DependencyProperty.Register(
        nameof(Blurb), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty, OnBlurbChanged));

    // TitleKey and BlurbKey exist because PageHeader is a UserControl, not a ContentControl (verified
    // against the WinUI winmd: it implements IUserControl, not IContentControl), so none of Loc's five
    // attached properties can dispatch on it the way they dispatch on TextBlock or ContentControl. Title
    // and Blurb keep meaning exactly what they always meant, literal display text set straight onto the
    // inner TextBlock; TitleKey and BlurbKey are the migrated form, resolved through Strings.Current. A
    // page sets one or the other, never both on purpose, but if both are set the key wins, matching how
    // a key always wins over inline text elsewhere in the recipe.
    public static readonly DependencyProperty TitleKeyProperty = DependencyProperty.Register(
        nameof(TitleKey), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty BlurbKeyProperty = DependencyProperty.Register(
        nameof(BlurbKey), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty, OnBlurbChanged));

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(PageHeader), new PropertyMetadata(null));

    public PageHeader()
    {
        InitializeComponent();
        ApplyTitle();
        ApplyBlurb();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Blurb
    {
        get => (string)GetValue(BlurbProperty);
        set => SetValue(BlurbProperty, value);
    }

    public string TitleKey
    {
        get => (string)GetValue(TitleKeyProperty);
        set => SetValue(TitleKeyProperty, value);
    }

    public string BlurbKey
    {
        get => (string)GetValue(BlurbKeyProperty);
        set => SetValue(BlurbKeyProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PageHeader)d).ApplyTitle();

    private static void OnBlurbChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PageHeader)d).ApplyBlurb();

    private void ApplyTitle() =>
        TitleText.Text = !string.IsNullOrEmpty(TitleKey) ? Strings.Current[TitleKey] : Title ?? string.Empty;

    private void ApplyBlurb()
    {
        var hasKey = !string.IsNullOrEmpty(BlurbKey);
        var resolved = hasKey ? Strings.Current[BlurbKey] : Blurb ?? string.Empty;
        var hasBlurb = !string.IsNullOrWhiteSpace(resolved);

        BlurbText.Text = resolved;
        WhyButton.Visibility = hasBlurb ? Visibility.Visible : Visibility.Collapsed;

        // The tooltip says what the page is for, not what the button does: hovering "Why?" is a
        // faster way to get the answer than opening its flyout, and "What this page is for." told
        // the user nothing they did not already know.
        ToolTipService.SetToolTip(WhyButton, hasBlurb ? resolved : null);
    }
}
