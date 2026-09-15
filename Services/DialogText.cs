using System.Threading;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace BannerlordEnvironmentManager.Services
{
    // A dialog's command area gives each button an equal share of the dialog's width, and a Button
    // handed a string draws it on one line and cuts off whatever does not fit: "Download as steamuser"
    // arrived as "Download as ste" with the account name beheaded and no way to read the rest. The
    // text BEM writes is BEM's to keep, so the string is replaced with a TextBlock that wraps, and
    // the whole of it goes into a tooltip besides.
    //
    // Done to the element after the dialog opens rather than through PrimaryButtonStyle, because
    // ContentDialog's own DefaultButton visual state replaces that style with the accent one and
    // would take any ContentTemplate set there with it. Content is never touched by a style, so this
    // survives every state the buttons go through, and the TextBlock inherits the foreground the
    // string was already being drawn in.
    //
    // Show a dialog through ShowAsync here rather than through ContentDialog.ShowAsync, so that no
    // new dialog has to remember any of this. DialogShowTests in the Core test project fails a file
    // that builds a ContentDialog more times than it routes one through this class.
    public static class DialogText
    {
        private static int shapeAlreadyReported;

        // The widest a dialog's content can be drawn: ContentDialogMaxWidth is 548, and it is set on
        // BackgroundElement, a Border whose own ContentDialogBorderWidth is 1 a side, inside which
        // ContentDialogPadding takes 24 a side. 548 - 2 - 48 leaves 498. A panel built in code asks
        // for a width so that a one-sentence dialog does not collapse toward the 320 px minimum, and
        // asking for more than this is not a wider dialog: the ScrollViewer in ContentDialog's own
        // template has horizontal scrolling disabled, so the overflow is cut off with no way to reach
        // it, and every paragraph wraps at a width the dialog will never show the right-hand end of.
        // DialogContentWidthTests fails a dialog that asks for more.
        //
        // High contrast draws a 2 px border and leaves 496, which is not what this is set to: two
        // more pixels of a wrapped paragraph go missing in that theme, and sizing every dialog on
        // this machine for a theme it is not in would be the worse trade.
        public const double ContentWidth = 498;

        // A title is a ContentControl, so it can be given an element instead of a string: a version
        // name plus a DLC name is long enough to need one.
        public static TextBlock Title(string text) => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = false
        };

        // Drop-in for dialog.ShowAsync(): same return type, same await, and the text is kept whole.
        public static IAsyncOperation<ContentDialogResult> ShowAsync(ContentDialog dialog)
        {
            KeepTextWhole(dialog);

            return dialog.ShowAsync();
        }

        public static void KeepTextWhole(ContentDialog dialog)
        {
            ArgumentNullException.ThrowIfNull(dialog);

            if (dialog.Title is string title && title.Trim().Length > 0)
                dialog.Title = Title(title);

            dialog.Opened += (sender, _) => WrapButtonText(sender);
        }

        private static void WrapButtonText(ContentDialog dialog)
        {
            var declared = new[] { dialog.PrimaryButtonText, dialog.SecondaryButtonText, dialog.CloseButtonText }
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var button in Descendants(dialog).OfType<Button>())
            {
                if (button.Content is not string text || text.Trim().Length == 0)
                    continue;

                declared.Remove(text);

                ToolTipService.SetToolTip(button, text);

                button.Content = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    IsTextSelectionEnabled = false
                };
            }

            ReportButtonsNotReached(declared);
        }

        // The walk depends on the shape of ContentDialog's own template, which BEM does not own. If
        // that shape ever changes the buttons go back to being cut off, and without this the only
        // evidence would be the user noticing a beheaded label again. Reported once per session
        // because it is one defect in the template, not one per dialog.
        private static void ReportButtonsNotReached(IReadOnlyCollection<string> missed)
        {
            if (missed.Count == 0 || Interlocked.Exchange(ref shapeAlreadyReported, 1) == 1)
                return;

            LoggingService.Log(
                "DialogText did not reach these dialog buttons: "
                + string.Join(", ", missed.Select(text => "'" + text + "'"))
                + ". ContentDialog no longer holds its buttons where the visual-tree walk looks, so long "
                + "button text is being cut off again.",
                LogLevel.Warn);
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);

            for (var index = 0; index < count; index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);

                yield return child;

                foreach (var descendant in Descendants(child))
                    yield return descendant;
            }
        }
    }
}
