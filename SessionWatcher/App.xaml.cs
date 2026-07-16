using Microsoft.UI.Xaml;
using SessionWatcher.Services;
using SessionWatcher.ViewModels;

namespace SessionWatcher;

public partial class App : Microsoft.UI.Xaml.Application
{
    public App()
    {
        InitializeComponent();
        Runtime = new AppRuntime();
        ViewModel = new DashboardViewModel(Runtime);
    }

    public static new App Current => (App)Microsoft.UI.Xaml.Application.Current;

    public AppRuntime Runtime { get; }

    public DashboardViewModel ViewModel { get; }

    public MainWindow? MainWindow { get; private set; }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        await Runtime.InitializeAsync(CancellationToken.None);
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
