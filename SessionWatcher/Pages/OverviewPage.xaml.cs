using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SessionWatcher.ViewModels;

namespace SessionWatcher.Pages;

public sealed partial class OverviewPage : Page
{
    public OverviewPage()
    {
        InitializeComponent();
    }

    public DashboardViewModel ViewModel => App.Current.ViewModel;

    private async void OnRefreshClicked(object sender, RoutedEventArgs args) =>
        await ViewModel.RefreshAsync(CancellationToken.None);
}
