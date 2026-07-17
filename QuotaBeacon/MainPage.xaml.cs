using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuotaBeacon.Pages;

namespace QuotaBeacon;

public sealed partial class MainPage : Page
{
    private readonly DispatcherQueueTimer _refreshTimer;

    public MainPage()
    {
        InitializeComponent();
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.IsRepeating = true;
        _refreshTimer.Tick += async (_, _) => await App.Current.ViewModel.RefreshAsync(CancellationToken.None);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        App.Current.Runtime.SettingsChanged += OnSettingsChanged;
        ConfigureRefreshTimer();
        ContentFrame.Navigate(typeof(OverviewPage));
        await App.Current.ViewModel.RefreshAsync(CancellationToken.None);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _refreshTimer.Stop();
        App.Current.Runtime.SettingsChanged -= OnSettingsChanged;
    }

    private async void OnSettingsChanged(object? sender, EventArgs args)
    {
        ConfigureRefreshTimer();
        await App.Current.ViewModel.RefreshAsync(CancellationToken.None);
    }

    private void ConfigureRefreshTimer()
    {
        _refreshTimer.Interval = TimeSpan.FromMinutes(
            App.Current.Runtime.CurrentSettings.RefreshIntervalMinutes);
        _refreshTimer.Start();
    }

    private void OnNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItemContainer is not NavigationViewItem item)
        {
            return;
        }

        var page = item.Tag?.ToString() switch
        {
            "history" => typeof(HistoryPage),
            "plugins" => typeof(PluginsPage),
            _ => typeof(OverviewPage)
        };
        if (ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page);
        }
    }
}
