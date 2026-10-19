namespace BannerlordEnvironmentManager.ViewModels
{
    public partial class BaseViewModel : ObservableObject
    {
        public BaseViewModel()
        {
            Title = string.Empty;
        }

        [ObservableProperty]
        public partial string Title { get; set; }
    }
}



