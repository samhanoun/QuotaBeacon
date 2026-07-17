using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using QuotaBeacon.Core.Alerts;
using QuotaBeacon.Core.Analytics;
using QuotaBeacon.Core.Models;
using QuotaBeacon.Core.Presentation;
using QuotaBeacon.Services;

namespace QuotaBeacon.ViewModels;

public sealed record HistoryRowModel(
    string ProviderName,
    string ObservedText,
    string SourceText,
    string Summary);

public sealed class DashboardRefreshEventArgs(
    IReadOnlyList<ProviderSnapshot> snapshots,
    IReadOnlyList<UsageAlert> alerts) : EventArgs
{
    public IReadOnlyList<ProviderSnapshot> Snapshots { get; } = snapshots;

    public IReadOnlyList<UsageAlert> Alerts { get; } = alerts;
}

public sealed class DashboardViewModel(AppRuntime runtime) : INotifyPropertyChanged
{
    private readonly object _refreshSync = new();
    private Task? _refreshLoopTask;
    private bool _refreshRequested;
    private bool _isRefreshing;
    private string _statusText = "Waiting for the first refresh";
    private bool _hasNoProviders = true;
    private bool _hasLocalAnalytics;
    private bool _isCodexEnabled = true;
    private AnalyticsSummaryModel _analytics = AnalyticsSummaryModel.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<DashboardRefreshEventArgs>? Refreshed;

    public ObservableCollection<ProviderCardModel> Providers { get; } = [];

    public ObservableCollection<HistoryRowModel> History { get; } = [];

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetField(ref _isRefreshing, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public bool HasNoProviders
    {
        get => _hasNoProviders;
        private set => SetField(ref _hasNoProviders, value);
    }

    public bool HasLocalAnalytics
    {
        get => _hasLocalAnalytics;
        private set => SetField(ref _hasLocalAnalytics, value);
    }

    public bool IsCodexEnabled
    {
        get => _isCodexEnabled;
        private set => SetField(ref _isCodexEnabled, value);
    }

    public AnalyticsSummaryModel Analytics
    {
        get => _analytics;
        private set => SetField(ref _analytics, value);
    }

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyProviderVisibility(runtime.EnabledProviderIds);
        Task refreshTask;
        lock (_refreshSync)
        {
            _refreshRequested = true;
            if (_refreshLoopTask is null || _refreshLoopTask.IsCompleted)
            {
                _refreshLoopTask = RunRefreshLoopAsync();
            }

            refreshTask = _refreshLoopTask;
        }

        // A caller may stop awaiting the shared refresh, but it must not cancel work requested
        // by the timer, Settings, or another caller that has coalesced into the same loop.
        return cancellationToken.CanBeCanceled
            ? refreshTask.WaitAsync(cancellationToken)
            : refreshTask;
    }

    private async Task RunRefreshLoopAsync()
    {
        try
        {
            while (true)
            {
                lock (_refreshSync)
                {
                    _refreshRequested = false;
                }

                await RefreshOnceAsync(CancellationToken.None);

                lock (_refreshSync)
                {
                    if (!_refreshRequested)
                    {
                        _refreshLoopTask = null;
                        return;
                    }
                }
            }
        }
        catch
        {
            lock (_refreshSync)
            {
                _refreshRequested = false;
                _refreshLoopTask = null;
            }

            throw;
        }
    }

    private async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsRefreshing = true;
            StatusText = "Refreshing provider usage…";
            var enabledProviderIds = runtime.EnabledProviderIds;
            ApplyProviderVisibility(enabledProviderIds);
            var providerTask = runtime.Coordinator.RefreshAsync(enabledProviderIds, cancellationToken);
            var analyticsTask = IsCodexEnabled
                ? ReadAnalyticsSafelyAsync(cancellationToken)
                : Task.FromResult<CodexLocalAnalyticsSnapshot?>(null);
            var snapshots = await providerTask;
            var localAnalytics = await analyticsTask;
            if (!SameProviders(enabledProviderIds, runtime.EnabledProviderIds))
            {
                // Settings changed while providers were running. The coalesced follow-up owns
                // projection so a newly disabled provider never flashes or raises a stale alert.
                return;
            }

            var now = DateTimeOffset.UtcNow;

            Providers.Clear();
            foreach (var snapshot in snapshots)
            {
                Providers.Add(DashboardProjector.Project(snapshot, now));
            }

            HasNoProviders = enabledProviderIds.Count == 0;
            if (localAnalytics is not null)
            {
                Analytics = AnalyticsProjector.Project(localAnalytics);
                HasLocalAnalytics = localAnalytics.Last30Days.TotalTokens > 0;
            }
            else
            {
                Analytics = AnalyticsSummaryModel.Empty;
                HasLocalAnalytics = false;
            }
            var available = snapshots.Count(snapshot => snapshot.Status == SnapshotStatus.Available);
            StatusText = enabledProviderIds.Count == 0
                ? "All providers are disabled"
                : available == 0
                ? "No live usage is available yet"
                : $"{available} provider{(available == 1 ? string.Empty : "s")} updated at {now.ToLocalTime():t}";

            var alerts = snapshots.SelectMany(runtime.AlertMonitor.Observe).ToArray();
            Refreshed?.Invoke(this, new DashboardRefreshEventArgs(snapshots, alerts));
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private static bool SameProviders(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second) =>
        first.Count == second.Count &&
        first.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(second);

    private void ApplyProviderVisibility(IReadOnlyList<string> enabledProviderIds)
    {
        var enabled = enabledProviderIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IsCodexEnabled = enabled.Contains("codex");
        for (var index = Providers.Count - 1; index >= 0; index--)
        {
            if (!enabled.Contains(Providers[index].ProviderId))
            {
                Providers.RemoveAt(index);
            }
        }

        HasNoProviders = enabled.Count == 0;
        if (!IsCodexEnabled)
        {
            Analytics = AnalyticsSummaryModel.Empty;
            HasLocalAnalytics = false;
        }
    }

    private async Task<CodexLocalAnalyticsSnapshot?> ReadAnalyticsSafelyAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(
                () => runtime.LocalAnalytics.ReadAsync(cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {
        var snapshots = await runtime.History.ReadAsync(null, cancellationToken);
        History.Clear();
        foreach (var snapshot in snapshots.Take(250))
        {
            History.Add(new HistoryRowModel(
                snapshot.ProviderName,
                snapshot.ObservedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                snapshot.Source switch
                {
                    SnapshotSource.Live => "Live",
                    SnapshotSource.LocalFallback => "Local fallback",
                    _ => "Cached"
                },
                string.Join(
                    " · ",
                    snapshot.Windows.Select(window => $"{window.Label} {window.UsedPercent:0}%"))));
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
