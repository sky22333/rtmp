using System;
using System.Windows;
using StreamCapturePro.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace StreamCapturePro.Views
{
    public partial class MainWindow : FluentWindow
    {
        private readonly ISnackbarService _snackbarService;

        public MainWindow(
            ISnackbarService snackbarService,
            DashboardPage dashboardPage)
        {
            _snackbarService = snackbarService;

            InitializeComponent();

            PageHost.Content = dashboardPage;
            _snackbarService.SetSnackbarPresenter(RootSnackbar);
        }

        private void TrayOpen_Click(object sender, RoutedEventArgs e)
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        private void TrayExit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                this.Hide();
            }
            base.OnStateChanged(e);
        }
    }
}
