using System.Windows.Controls;
using StreamCapturePro.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace StreamCapturePro.Views.Pages
{
    public partial class DashboardPage : Page, INavigableView<DashboardViewModel>
    {
        public DashboardViewModel ViewModel { get; }

        public DashboardPage(DashboardViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = ViewModel;
            InitializeComponent();
        }
    }
}
