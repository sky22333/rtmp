using CommunityToolkit.Mvvm.ComponentModel;

namespace StreamCapturePro.ViewModels
{
    public partial class AboutViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _appVersion = "1.0.0"; // 可根据实际 Assembly 版本读取
    }
}
