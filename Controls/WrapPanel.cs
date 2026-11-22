using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace BannerlordEnvironmentManager.Controls;

// A row of buttons that wraps onto the next line instead of scrolling sideways. A horizontal scroller
// draws its bar over the buttons it holds, which covered their labels; text a user is meant to read
// is never covered, so a row that does not fit takes a second line.
public sealed class WrapPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(12d, OnLayoutChanged));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(8d, OnLayoutChanged));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    private static void OnLayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((WrapPanel)sender).InvalidateMeasure();

    // Measure never changes a child: replacing a button's content during layout invalidated the layout it
    // was in, and with the Health list's 190 rows that became a layout cycle that froze the window.
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }

        return Lay(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Lay(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Lay(double width, bool arrange)
    {
        double x = 0, y = 0, rowHeight = 0, widest = 0;
        var any = false;

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            var size = child.DesiredSize;

            if (any && x > 0 && x + size.Width > width)
            {
                y += rowHeight + RowSpacing;
                x = 0;
                rowHeight = 0;
            }

            if (arrange)
                child.Arrange(new Rect(x, y, Math.Min(size.Width, double.IsInfinity(width) ? size.Width : width), size.Height));

            x += size.Width + Spacing;
            widest = Math.Max(widest, x - Spacing);
            rowHeight = Math.Max(rowHeight, size.Height);
            any = true;
        }

        return new Size(double.IsInfinity(width) ? widest : Math.Min(widest, width), any ? y + rowHeight : 0);
    }
}
