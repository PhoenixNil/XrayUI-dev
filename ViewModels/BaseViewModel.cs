using CommunityToolkit.Mvvm.ComponentModel;

namespace XrayUI.ViewModels
{
    public partial class BaseViewModel : ObservableObject
    {
        public BaseViewModel()
        {
        }

        [ObservableProperty]
        private string title = string.Empty;
    }
}

