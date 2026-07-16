using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SessionWatcher.ViewModels;

namespace SessionWatcher.Pages;

public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public DashboardViewModel ViewModel => App.Current.ViewModel;

    private async void OnLoaded(object sender, RoutedEventArgs args) =>
        await ViewModel.LoadHistoryAsync(CancellationToken.None);

    private async void OnReloadClicked(object sender, RoutedEventArgs args) =>
        await ViewModel.LoadHistoryAsync(CancellationToken.None);
}
