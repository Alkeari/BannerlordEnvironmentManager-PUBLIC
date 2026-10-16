namespace BannerlordEnvironmentManager.Views
{
    public sealed partial class UnblockPage : Page
    {
        public UnblockPage()
        {
            InitializeComponent();
        }

        public UnblockFilesViewModel UnblockViewModel => ShellViewModels.Instance.Unblock;
    }
}
