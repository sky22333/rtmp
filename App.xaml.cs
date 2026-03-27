using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using StreamCapturePro.Core.Extractors;
using StreamCapturePro.Core.Utils;
using StreamCapturePro.Services;
using StreamCapturePro.ViewModels;
using StreamCapturePro.Views;
using StreamCapturePro.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Abstractions;

namespace StreamCapturePro
{
    public partial class App : Application
    {
        private static readonly ServiceProvider _serviceProvider = ConfigureServices();

        private static ServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection();

            services.AddSingleton<INavigationViewPageProvider, PageService>();
            services.AddSingleton<INavigationService, NavigationService>();
            services.AddSingleton<ISnackbarService, SnackbarService>();
            services.AddSingleton<IThemeService, ThemeService>();
            services.AddSingleton<ObsSyncService>();
            services.AddSingleton<ProcessScanOptionsService>();

            services.AddTransient<IStreamExtractor, ProcessMemoryExtractor>();
            services.AddTransient<IStreamExtractor, DouyinLogExtractor>();

            services.AddSingleton<MainWindow>();
            services.AddTransient<DashboardPage>();
            services.AddTransient<DashboardViewModel>();
            services.AddTransient<AboutPage>();
            services.AddTransient<AboutViewModel>();

            return services.BuildServiceProvider();
        }

        public static T GetService<T>() where T : class
        {
            var service = _serviceProvider.GetService(typeof(T)) as T;
            if (service == null)
            {
                throw new InvalidOperationException($"无法解析服务: {typeof(T)}");
            }
            return service;
        }

        public App()
        {
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        private void OnStartup(object sender, StartupEventArgs e)
        {
            try
            {
                var mainWindow = GetService<MainWindow>();
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"程序启动失败！\n错误信息：{ex.Message}\n堆栈：{ex.StackTrace}", "启动致命错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private void OnExit(object sender, ExitEventArgs e)
        {
            try
            {
                _serviceProvider.Dispose();
            }
            catch
            {
            }
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show($"UI 线程发生严重错误：\n{e.Exception.Message}", "系统错误", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                MessageBox.Show($"后台线程发生致命错误：\n{ex.Message}", "致命错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
