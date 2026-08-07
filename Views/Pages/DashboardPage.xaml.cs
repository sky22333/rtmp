using System.Windows.Controls;
using StreamCapturePro.ViewModels;

namespace StreamCapturePro.Views.Pages
{
    public partial class DashboardPage : UserControl
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
