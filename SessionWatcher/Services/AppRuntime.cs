using System.Net;
using SessionWatcher.Core.Alerts;
using SessionWatcher.Core.Analytics;
using SessionWatcher.Core.History;
using SessionWatcher.Core.Plugins;
using SessionWatcher.Core.Providers;
using SessionWatcher.Core.Providers.Claude;
using SessionWatcher.Core.Providers.Codex;
using SessionWatcher.Core.Services;
using SessionWatcher.Core.Settings;

namespace SessionWatcher.Services;

public sealed class AppRuntime : IDisposable
{
    private readonly HttpClient _claudeClient;
    private readonly JsonSettingsStore _settingsStore;

    public AppRuntime()
    {
        var localState = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        var preferredDataDirectory = Path.Combine(localState, "QuotaBeacon");
        var legacyDataDirectory = Path.Combine(localState, "SessionWatcher");
        DataDirectory = Directory.Exists(preferredDataDirectory) || !Directory.Exists(legacyDataDirectory)
            ? preferredDataDirectory
            : legacyDataDirectory;
        PluginDirectory = Path.Combine(DataDirectory, "plugins");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(PluginDirectory);

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        _claudeClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        var timeProvider = TimeProvider.System;
        var builtIns = new IUsageProvider[]
        {
            new ClaudeUsageProvider(new ClaudeCredentialReader(), _claudeClient, timeProvider),
            new CodexUsageProvider(
                new CodexAppServerSource(
                    new CodexProcessConnectionFactory(),
                    timeProvider,
                    TimeSpan.FromSeconds(12)),
                new CodexSessionLogSource(null, timeProvider),
                timeProvider)
        };

        Catalog = ProviderCatalog.Load(PluginDirectory, builtIns);
        LocalAnalytics = new CodexLocalAnalyticsReader(null, timeProvider);
        History = new JsonUsageHistoryStore(
            Path.Combine(DataDirectory, "history.json"),
            TimeSpan.FromDays(90),
            timeProvider);
        Coordinator = new UsageCoordinator(Catalog.Providers, History, timeProvider);
        AlertMonitor = new UsageAlertMonitor();
        _settingsStore = new JsonSettingsStore(Path.Combine(DataDirectory, "settings.json"));
    }

    public event EventHandler? SettingsChanged;

    public string DataDirectory { get; }

    public string PluginDirectory { get; }

    public ProviderCatalogResult Catalog { get; }

    public IUsageHistoryStore History { get; }

    public UsageCoordinator Coordinator { get; }

    public UsageAlertMonitor AlertMonitor { get; }

    public ILocalAnalyticsReader LocalAnalytics { get; }

    public AppSettings CurrentSettings { get; private set; } = AppSettings.Default;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        CurrentSettings = await _settingsStore.ReadAsync(cancellationToken);
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        CurrentSettings = settings.Normalize();
        await _settingsStore.WriteAsync(CurrentSettings, cancellationToken);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _claudeClient.Dispose();
    }
}
