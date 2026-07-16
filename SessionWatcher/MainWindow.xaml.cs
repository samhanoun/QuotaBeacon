using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SessionWatcher.Services;
using SessionWatcher.ViewModels;
using Windows.Graphics;

namespace SessionWatcher;

public sealed partial class MainWindow : Window
{
    private readonly TrayIconService _trayIcon;
    private bool _exitRequested;
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1080, 760));
        AppWindow.Closing += OnAppWindowClosing;

        _trayIcon = new TrayIconService(
            DispatcherQueue,
            ShowWindow,
            () => App.Current.ViewModel.RefreshAsync(CancellationToken.None),
            ExitApplication);
        App.Current.ViewModel.Refreshed += OnDashboardRefreshed;

        RootFrame.Navigate(typeof(MainPage));
    }

    private void OnDashboardRefreshed(object? sender, DashboardRefreshEventArgs args)
    {
        _trayIcon.Update(args.Snapshots);
        foreach (var alert in args.Alerts)
        {
            _trayIcon.ShowAlert(alert);
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_exitRequested && App.Current.Runtime.CurrentSettings.MinimizeToTray)
        {
            args.Cancel = true;
            AppWindow.Hide();
            return;
        }

        DisposeServices();
    }

    private void ShowWindow()
    {
        AppWindow.Show(activateWindow: true);
        Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        DisposeServices();
        App.Current.Exit();
    }

    private void DisposeServices()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        App.Current.ViewModel.Refreshed -= OnDashboardRefreshed;
        _trayIcon.Dispose();
        App.Current.Runtime.Dispose();
    }
}
