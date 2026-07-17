using System.Net;
using QuotaBeacon.Core.Alerts;
using QuotaBeacon.Core.Analytics;
using QuotaBeacon.Core.History;
using QuotaBeacon.Core.Plugins;
using QuotaBeacon.Core.Providers;
using QuotaBeacon.Core.Providers.Claude;
using QuotaBeacon.Core.Providers.Codex;
using QuotaBeacon.Core.Providers.Google;
using QuotaBeacon.Core.Services;
using QuotaBeacon.Core.Settings;

namespace QuotaBeacon.Services;

public sealed class AppRuntime : IDisposable
{
    private readonly HttpClient _claudeClient;
    private readonly JsonSettingsStore _settingsStore;

    public AppRuntime()
    {
        var localState = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        var preferredDataDirectory = Path.Combine(localState, "QuotaBeacon");
        // Preserve the original data folder so existing installations upgrade without losing history.
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
                timeProvider),
            new GeminiUsageProvider(
                new OfficialCliUsageSource(
                    ["gemini.exe", "gemini.cmd", "gemini.bat"],
                    "QUOTABEACON_GEMINI_PATH",
                    "/model",
                    "Gemini CLI is not installed or is not on PATH. Install the official CLI, then sign in and refresh."),
                timeProvider),
            new AntigravityUsageProvider(
                new OfficialCliUsageSource(
                    ["agy.exe", "agy.cmd", "agy.bat"],
                    "QUOTABEACON_ANTIGRAVITY_PATH",
                    "/usage",
                    "Antigravity CLI (agy) is not installed or is not on PATH. Install the official CLI, then sign in and refresh."),
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

    public IReadOnlyList<IUsageProvider> Providers => Catalog.Providers;

    public IReadOnlyList<string> EnabledProviderIds => Catalog.Providers
        .Where(provider => CurrentSettings.IsProviderEnabled(provider.Id))
        .Select(provider => provider.Id)
        .ToArray();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        CurrentSettings = await _settingsStore.ReadAsync(cancellationToken);
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var normalized = settings.Normalize();
        await _settingsStore.WriteAsync(normalized, cancellationToken);
        CurrentSettings = normalized;
        foreach (var provider in Catalog.Providers.Where(
                     provider => !normalized.IsProviderEnabled(provider.Id)))
        {
            AlertMonitor.EvictProvider(provider.Id);
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _claudeClient.Dispose();
    }
}
