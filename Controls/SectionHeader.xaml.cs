using BannerlordEnvironmentManager.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BannerlordEnvironmentManager.Controls;

public sealed partial class SectionHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SectionHeader), new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty BlurbProperty = DependencyProperty.Register(
        nameof(Blurb), typeof(string), typeof(SectionHeader), new PropertyMetadata(string.Empty, OnBlurbChanged));

    // TitleKey and BlurbKey exist because SectionHeader is a UserControl, not a ContentControl (verified
    // against the WinUI winmd: it implements IUserControl, not IContentControl), so none of Loc's five
    // attached properties can dispatch on it the way they dispatch on TextBlock or ContentControl. Title
    // and Blurb keep meaning exactly what they always meant, literal display text set straight onto the
    // inner TextBlock; TitleKey and BlurbKey are the migrated form, resolved through Strings.Current. A
    // page sets one or the other, never both on purpose, but if both are set the key wins, matching how
    // a key always wins over inline text elsewhere in the recipe.
    public static readonly DependencyProperty TitleKeyProperty = DependencyProperty.Register(
        nameof(TitleKey), typeof(string), typeof(SectionHeader), new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty BlurbKeyProperty = DependencyProperty.Register(
        nameof(BlurbKey), typeof(string), typeof(SectionHeader), new PropertyMetadata(string.Empty, OnBlurbChanged));

    public SectionHeader()
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

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SectionHeader)d).ApplyTitle();

    private static void OnBlurbChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SectionHeader)d).ApplyBlurb();

    // The title is trimmed rather than wrapped, so the whole of it has to be reachable on hover.
    private void ApplyTitle()
    {
        var hasKey = !string.IsNullOrEmpty(TitleKey);
        var resolved = hasKey ? Strings.Current[TitleKey] : Title ?? string.Empty;
        TitleText.Text = resolved;
        ToolTipService.SetToolTip(TitleText, string.IsNullOrWhiteSpace(resolved) ? null : resolved);
    }

    private void ApplyBlurb()
    {
        var hasKey = !string.IsNullOrEmpty(BlurbKey);
        var resolved = hasKey ? Strings.Current[BlurbKey] : Blurb ?? string.Empty;
        var hasBlurb = !string.IsNullOrWhiteSpace(resolved);

        BlurbText.Text = resolved;
        WhyButton.Visibility = hasBlurb ? Visibility.Visible : Visibility.Collapsed;

        // The tooltip answers the question the button names, in one hover, rather than saying
        // "What this section is about.".
        ToolTipService.SetToolTip(WhyButton, hasBlurb ? resolved : null);
    }
}
