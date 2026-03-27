using System.Windows.Controls;
using StreamCapturePro.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace StreamCapturePro.Views.Pages
{
    public partial class AboutPage : Page, INavigableView<AboutViewModel>
    {
        public AboutViewModel ViewModel { get; }

        public AboutPage(AboutViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = ViewModel;
            InitializeComponent();
        }
    }
}
