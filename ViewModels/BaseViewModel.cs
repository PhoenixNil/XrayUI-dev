namespace XrayUI.ViewModels
{
    public partial class BaseViewModel : ObservableObject
    {
        private string _title = string.Empty;

        public BaseViewModel()
        {
        }

        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }
    }
}

