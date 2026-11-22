using BannerlordEnvironmentManager.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace BannerlordEnvironmentManager.Controls;

// The card every page region is built from (redesign proposal 1.2): a fixed header with a gold title,
// an optional meta line and header actions; an optional fixed toolbar; a body that scrolls inside the
// card's share of the page; and an optional footer for the card's one or two actions. The card never
// sizes itself to its content: its height is the grid row it sits in.
[ContentProperty(Name = nameof(Body))]
public sealed partial class CardShell : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(CardShell), new PropertyMetadata(string.Empty, OnHeaderChanged));

    // A catalog key resolved once, the way PageHeader's TitleKey is, for a title written in XAML.
    public static readonly DependencyProperty TitleKeyProperty = DependencyProperty.Register(
        nameof(TitleKey), typeof(string), typeof(CardShell), new PropertyMetadata(string.Empty, OnHeaderChanged));

    public static readonly DependencyProperty MetaProperty = DependencyProperty.Register(
        nameof(Meta), typeof(string), typeof(CardShell), new PropertyMetadata(string.Empty, OnHeaderChanged));

    public static readonly DependencyProperty IsPrimaryProperty = DependencyProperty.Register(
        nameof(IsPrimary), typeof(bool), typeof(CardShell), new PropertyMetadata(false, OnHeaderChanged));

    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(CardShell), new PropertyMetadata(false, OnHeaderChanged));

    public static readonly DependencyProperty BannerProperty = DependencyProperty.Register(
        nameof(Banner), typeof(string), typeof(CardShell), new PropertyMetadata(string.Empty, OnHeaderChanged));

    public static readonly DependencyProperty HeaderActionsProperty = DependencyProperty.Register(
        nameof(HeaderActions), typeof(object), typeof(CardShell), new PropertyMetadata(null, OnHeaderChanged));

    public static readonly DependencyProperty ToolbarProperty = DependencyProperty.Register(
        nameof(Toolbar), typeof(object), typeof(CardShell), new PropertyMetadata(null, OnSlotsChanged));

    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(object), typeof(CardShell), new PropertyMetadata(null));

    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
        nameof(Footer), typeof(object), typeof(CardShell), new PropertyMetadata(null, OnSlotsChanged));

    // True: the card scrolls its body. False: the body is handed the card's own height, for a body
    // that scrolls itself (a ListView or a nested grid of cards), so no bar ever sits inside a bar.
    public static readonly DependencyProperty BodyScrollsProperty = DependencyProperty.Register(
        nameof(BodyScrolls), typeof(bool), typeof(CardShell), new PropertyMetadata(true, OnSlotsChanged));

    // False drops the body padding, for a list whose rows run to the card's edges.
    public static readonly DependencyProperty BodyPaddedProperty = DependencyProperty.Register(
        nameof(BodyPadded), typeof(bool), typeof(CardShell), new PropertyMetadata(true, OnSlotsChanged));

    public CardShell()
    {
        InitializeComponent();
        ApplyHeader();
        ApplySlots();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string TitleKey
    {
        get => (string)GetValue(TitleKeyProperty);
        set => SetValue(TitleKeyProperty, value);
    }

    public string Meta
    {
        get => (string)GetValue(MetaProperty);
        set => SetValue(MetaProperty, value);
    }

    public bool IsPrimary
    {
        get => (bool)GetValue(IsPrimaryProperty);
        set => SetValue(IsPrimaryProperty, value);
    }

    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    public string Banner
    {
        get => (string)GetValue(BannerProperty);
        set => SetValue(BannerProperty, value);
    }

    public object? HeaderActions
    {
        get => GetValue(HeaderActionsProperty);
        set => SetValue(HeaderActionsProperty, value);
    }

    public object? Toolbar
    {
        get => GetValue(ToolbarProperty);
        set => SetValue(ToolbarProperty, value);
    }

    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    public bool BodyScrolls
    {
        get => (bool)GetValue(BodyScrollsProperty);
        set => SetValue(BodyScrollsProperty, value);
    }

    public bool BodyPadded
    {
        get => (bool)GetValue(BodyPaddedProperty);
        set => SetValue(BodyPaddedProperty, value);
    }

    private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        _ = e;
        ((CardShell)d).ApplyHeader();
    }

    private static void OnSlotsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        _ = e;
        ((CardShell)d).ApplySlots();
    }

    private void ApplyHeader()
    {
        var title = string.IsNullOrEmpty(TitleKey) ? Title : Strings.Current[TitleKey];

        TitleText.Text = title;
        ToolTipService.SetToolTip(TitleText, string.IsNullOrEmpty(title) ? null : title);

        MetaText.Text = Meta;
        MetaText.Visibility = string.IsNullOrEmpty(Meta) ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(MetaText, string.IsNullOrEmpty(Meta) ? null : Meta);

        PrimaryMarker.Text = "\u25C6";
        PrimaryMarker.Visibility = IsPrimary ? Visibility.Visible : Visibility.Collapsed;

        // A card with no title and no header actions gives its header row back to the body.
        HeaderRow.Visibility = string.IsNullOrEmpty(title) && HeaderActions is null
            ? Visibility.Collapsed
            : Visibility.Visible;

        BusyBar.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;

        // Accept the risk: a banner accepted once is not shown again in its own words.
        var banner = ViewModels.AcceptedNotices.Unless(BannerCategory, Banner);
        BannerText.Text = banner;
        BannerRow.Visibility = string.IsNullOrEmpty(banner) ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(BannerText, string.IsNullOrEmpty(banner) ? null : banner);
    }

    private const string BannerCategory = "Card";

    private void OnAcceptBanner(object sender, RoutedEventArgs e)
    {
        ViewModels.AcceptedNotices.Accept(BannerCategory, Banner);
        BannerRow.Visibility = Visibility.Collapsed;
    }

    // The body host moves between the scroller and the bare grid rather than being duplicated: a
    // UIElement has one parent.
    private void ApplySlots()
    {
        ToolbarHost.Visibility = Toolbar is null ? Visibility.Collapsed : Visibility.Visible;
        FooterRow.Visibility = Footer is null ? Visibility.Collapsed : Visibility.Visible;

        BodyHost.Padding = BodyPadded
            ? (Thickness)Application.Current.Resources["AlkCardBodyPadding"]
            : new Thickness(0);

        if (BodyScrolls)
        {
            if (ReferenceEquals(BodyScroller.Content, BodyHost))
                return;

            BodyRegion.Children.Remove(BodyHost);
            BodyScroller.Content = BodyHost;
            BodyScroller.Visibility = Visibility.Visible;
            return;
        }

        if (BodyRegion.Children.Contains(BodyHost))
            return;

        BodyScroller.Content = null;
        BodyScroller.Visibility = Visibility.Collapsed;
        BodyRegion.Children.Add(BodyHost);
    }
}
