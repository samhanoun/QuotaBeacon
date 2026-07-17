using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SessionWatcher.ViewModels;

namespace SessionWatcher.Pages;

public sealed partial class OverviewPage : Page
{
    public OverviewPage()
    {
        InitializeComponent();
        SizeChanged += OnPageSizeChanged;
    }

    // WinUI XAML binds to this instance property.
#pragma warning disable CA1822
    public DashboardViewModel ViewModel => App.Current.ViewModel;
#pragma warning restore CA1822

    private async void OnRefreshClicked(object sender, RoutedEventArgs args) =>
        await ViewModel.RefreshAsync(CancellationToken.None);

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var wideMetrics = args.NewSize.Width >= 900;
        Grid.SetColumn(TodayMetric, 0);
        Grid.SetRow(TodayMetric, 0);
        Grid.SetColumnSpan(TodayMetric, wideMetrics ? 1 : 2);
        Grid.SetColumn(MonthMetric, wideMetrics ? 1 : 2);
        Grid.SetRow(MonthMetric, 0);
        Grid.SetColumnSpan(MonthMetric, wideMetrics ? 1 : 2);
        Grid.SetColumn(CostMetric, wideMetrics ? 2 : 0);
        Grid.SetRow(CostMetric, wideMetrics ? 0 : 1);
        Grid.SetColumnSpan(CostMetric, wideMetrics ? 1 : 2);
        Grid.SetColumn(CacheMetric, wideMetrics ? 3 : 2);
        Grid.SetRow(CacheMetric, wideMetrics ? 0 : 1);
        Grid.SetColumnSpan(CacheMetric, wideMetrics ? 1 : 2);

        var wideAnalytics = args.NewSize.Width >= 960;
        Grid.SetColumn(ActivityCard, 0);
        Grid.SetRow(ActivityCard, 0);
        Grid.SetColumnSpan(ActivityCard, wideAnalytics ? 1 : 2);
        Grid.SetColumn(ModelsCard, wideAnalytics ? 1 : 0);
        Grid.SetRow(ModelsCard, wideAnalytics ? 0 : 1);
        Grid.SetColumnSpan(ModelsCard, wideAnalytics ? 1 : 2);
    }
}
