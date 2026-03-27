using System;
using System.Windows;
using StreamCapturePro.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

using Wpf.Ui.Appearance;

namespace StreamCapturePro.Views
{
    public partial class MainWindow : FluentWindow
    {
        private readonly INavigationService _navigationService;
        private readonly ISnackbarService _snackbarService;
        private readonly INavigationViewPageProvider _pageProvider;
        private readonly IThemeService _themeService;

        public MainWindow(
            INavigationService navigationService,
            ISnackbarService snackbarService,
            INavigationViewPageProvider pageProvider,
            IThemeService themeService)
        {
            _navigationService = navigationService;
            _snackbarService = snackbarService;
            _pageProvider = pageProvider;
            _themeService = themeService;

            InitializeComponent();

            _themeService.SetTheme(ApplicationTheme.Unknown); // Auto detect system theme
            
            _navigationService.SetNavigationControl(RootNavigation);
            RootNavigation.SetPageProviderService(_pageProvider);
            _snackbarService.SetSnackbarPresenter(RootSnackbar);
            this.Loaded += OnWindowLoaded;
            this.Unloaded += OnWindowUnloaded;
        }

        private void OnWindowUnloaded(object sender, RoutedEventArgs e)
        {
            this.Loaded -= OnWindowLoaded;
            this.Unloaded -= OnWindowUnloaded;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            _navigationService.Navigate(typeof(DashboardPage));
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
