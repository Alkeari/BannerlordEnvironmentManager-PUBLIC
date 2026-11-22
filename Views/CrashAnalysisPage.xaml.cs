using BannerlordEnvironmentManager.Core.Localization;
using BannerlordEnvironmentManager.Services;

namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class CrashAnalysisPage : Page
    {
        public CrashAnalysisPage()
        {
            InitializeComponent();
            Loaded += async (_, _) => await ViewModel.RefreshAsync();
        }

        public CrashAnalysisViewModel ViewModel { get; } = new();

        // Publishing a report cannot be taken back, so the button only builds it. The dialog shows
        // the owner exactly what would go and an upload happens only on its Upload button.
        private async void OnShareClick(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            try
            {
                if (!await ViewModel.PrepareShareAsync())
                    return;

                var strings = Strings.Current;

                var panel = new StackPanel { Spacing = 12, Width = DialogText.ContentWidth };

                panel.Children.Add(new TextBlock
                {
                    Text = strings["CrashView.Share.Dialog.Body"],
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = false
                });
                panel.Children.Add(new TextBlock
                {
                    Text = ViewModel.ShareSummary,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = false
                });
                panel.Children.Add(new TextBlock
                {
                    Text = strings["CrashView.Share.Dialog.Lead"],
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = false
                });
                panel.Children.Add(new ScrollViewer
                {
                    MaxHeight = 220,
                    HorizontalScrollMode = ScrollMode.Enabled,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock
                    {
                        Text = ViewModel.SharePayload,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                        TextWrapping = TextWrapping.NoWrap,
                        IsTextSelectionEnabled = true
                    }
                });

                var dialog = new ContentDialog
                {
                    Title = DialogText.Title(strings["CrashView.Share.Dialog.Title"]),
                    Content = panel,
                    PrimaryButtonText = strings["CrashView.Share.Dialog.Primary"],
                    CloseButtonText = strings["CrashView.Share.Dialog.Close"],
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot
                };

                if (await DialogText.ShowAsync(dialog) == ContentDialogResult.Primary)
                    await ViewModel.UploadShareAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ViewModel.ShowShareFailure(ex);
            }
        }
    }
}
