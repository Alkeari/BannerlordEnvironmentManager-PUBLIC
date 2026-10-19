using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace BannerlordEnvironmentManager.Controls;

// The four-band page frame. Identity, commands, content, status. The identity band is the shared
// PageHeader; Body is what the frame scrolls; Status is the one mono line every page carries.
// The body slot is named Body rather than Content because UserControl already owns a Content
// dependency property that the markup is better served leaving alone.
[ContentProperty(Name = nameof(Body))]
public sealed partial class PageShell : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageShell), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty BlurbProperty = DependencyProperty.Register(
        nameof(Blurb), typeof(string), typeof(PageShell), new PropertyMetadata(string.Empty));

    // TitleKey and BlurbKey exist for the same reason PageHeader's do, and are passed straight
    // through to the inner PageHeader (IdentityBand in PageShell.xaml), which already resolves a key
    // over a literal. PageShell has no TextBlock of its own for Title or Blurb to resolve here.
    public static readonly DependencyProperty TitleKeyProperty = DependencyProperty.Register(
        nameof(TitleKey), typeof(string), typeof(PageShell), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty BlurbKeyProperty = DependencyProperty.Register(
        nameof(BlurbKey), typeof(string), typeof(PageShell), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(PageShell), new PropertyMetadata(null));

    public static readonly DependencyProperty CommandsProperty = DependencyProperty.Register(
        nameof(Commands), typeof(object), typeof(PageShell), new PropertyMetadata(null));

    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(object), typeof(PageShell), new PropertyMetadata(null));

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(string), typeof(PageShell), new PropertyMetadata(string.Empty));

    // True keeps the body inside the frame's ScrollViewer, which is what every page using the frame
    // was written against. False hands the body the frame's own height: a page that gives its primary
    // region a star row with a floor, or that already scrolls something of its own, needs a body that
    // is measured rather than one handed an unbounded height and a second scrollbar.
    public static readonly DependencyProperty BodyScrollsProperty = DependencyProperty.Register(
        nameof(BodyScrolls), typeof(bool), typeof(PageShell), new PropertyMetadata(true, OnBodyScrollsChanged));

    public PageShell()
    {
        InitializeComponent();
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

    public object? Commands
    {
        get => GetValue(CommandsProperty);
        set => SetValue(CommandsProperty, value);
    }

    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public bool BodyScrolls
    {
        get => (bool)GetValue(BodyScrollsProperty);
        set => SetValue(BodyScrollsProperty, value);
    }

    private static void OnBodyScrollsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        _ = args;
        ((PageShell)sender).ApplyBodyScrolling();
    }

    // The body host is moved between the two slots rather than duplicated: a UIElement has one parent,
    // so a second presenter bound to the same Body would take the content away from the first.
    private void ApplyBodyScrolling()
    {
        if (BodyScrolls)
        {
            if (ReferenceEquals(ContentScroller.Content, BodyHost))
                return;

            BodyRegion.Children.Remove(BodyHost);
            ContentScroller.Content = BodyHost;
            ContentScroller.Visibility = Visibility.Visible;
            return;
        }

        if (BodyRegion.Children.Contains(BodyHost))
            return;

        ContentScroller.Content = null;
        ContentScroller.Visibility = Visibility.Collapsed;
        BodyRegion.Children.Add(BodyHost);
    }
}
