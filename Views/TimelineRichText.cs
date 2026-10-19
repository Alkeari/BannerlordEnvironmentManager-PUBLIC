using BannerlordEnvironmentManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace BannerlordEnvironmentManager.Views;

// TextBlock.Inlines has no setter to reach through a plain x:Bind - it is a mutable collection you add
// to, not a value you assign - so a timeline row's severity-colored headline and its optional gray
// detail line were two separate TextBlocks. WinUI cannot carry a text selection across separate
// sibling TextBlocks, so a row's own two lines could never be selected together. This attached
// property rebuilds one TextBlock's Inlines from the row every time it is set, which keeps both colors
// while making the whole entry one continuous selectable block.
public static class TimelineRichText
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.RegisterAttached(
        "Row",
        typeof(LaunchTimelineRowViewModel),
        typeof(TimelineRichText),
        new PropertyMetadata(null, OnRowChanged));

    public static void SetRow(TextBlock element, LaunchTimelineRowViewModel? value) =>
        element.SetValue(RowProperty, value);

    public static LaunchTimelineRowViewModel? GetRow(TextBlock element) =>
        (LaunchTimelineRowViewModel?)element.GetValue(RowProperty);

    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
            return;

        textBlock.Inlines.Clear();

        if (e.NewValue is not LaunchTimelineRowViewModel row)
            return;

        textBlock.Inlines.Add(new Run { Text = row.Headline, Foreground = row.HeadlineBrush });

        if (row.Detail.Length == 0)
            return;

        textBlock.Inlines.Add(new LineBreak());
        textBlock.Inlines.Add(new Run
        {
            Text = row.Detail,
            Foreground = (Brush)Application.Current.Resources["AlkSteelGrayBrush"]
        });
    }
}
