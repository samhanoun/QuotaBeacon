using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SessionWatcher.Core.Alerts;
using SessionWatcher.Core.Models;
using SessionWatcher.Core.Presentation;
using SessionWatcher.Services;

namespace SessionWatcher.ViewModels;

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
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private bool _isRefreshing;
    private string _statusText = "Waiting for the first refresh";
    private bool _hasNoProviders = true;

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

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            IsRefreshing = true;
            StatusText = "Refreshing provider usage…";
            var snapshots = await runtime.Coordinator.RefreshAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;

            Providers.Clear();
            foreach (var snapshot in snapshots)
            {
                Providers.Add(DashboardProjector.Project(snapshot, now));
            }

            HasNoProviders = Providers.Count == 0;
            var available = snapshots.Count(snapshot => snapshot.Status == SnapshotStatus.Available);
            StatusText = available == 0
                ? "No live usage is available yet"
                : $"{available} provider{(available == 1 ? string.Empty : "s")} updated at {now.ToLocalTime():t}";

            var alerts = snapshots.SelectMany(runtime.AlertMonitor.Observe).ToArray();
            Refreshed?.Invoke(this, new DashboardRefreshEventArgs(snapshots, alerts));
        }
        finally
        {
            IsRefreshing = false;
            _refreshGate.Release();
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
                snapshot.ObservedAt.ToLocalTime().ToString("g"),
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
