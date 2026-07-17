using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using QuotaBeacon.Services;
using QuotaBeacon.ViewModels;
using Windows.Graphics;

namespace QuotaBeacon;

public sealed partial class MainWindow : Window, IDisposable
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
        var workArea = DisplayArea
            .GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)
            .WorkArea;
        AppWindow.Resize(new SizeInt32(
            Math.Max(640, Math.Min(1180, (int)(workArea.Width * 0.9))),
            Math.Max(600, Math.Min(900, (int)(workArea.Height * 0.9)))));
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

    public void Dispose() => DisposeServices();
}
