using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace BannerlordEnvironmentManager.Controls;

// An indeterminate ProgressBar animates its moving segment past its own edges, and WinUI does not
// clip it, so inside a bar narrower than its container the segment ran on across the next column.
// Every ProgressBar in the app gets this through the implicit style in App.xaml.
public static class ClipToBounds
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ClipToBounds), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        element.SizeChanged -= OnSizeChanged;

        if (e.NewValue is true)
        {
            element.SizeChanged += OnSizeChanged;
            Apply(element);
        }
        else
        {
            element.Clip = null;
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement element) =>
        element.Clip = new RectangleGeometry { Rect = new Rect(0, 0, element.ActualWidth, element.ActualHeight) };
}
