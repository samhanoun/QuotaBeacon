using SessionWatcher.Core.History;
using SessionWatcher.Core.Models;
using SessionWatcher.Core.Providers;
using SessionWatcher.Core.Services;

namespace SessionWatcher.Core.Tests;

public sealed class UsageCoordinatorTests
{
    [Fact]
    public async Task Refresh_isolates_provider_failures_and_saves_successful_snapshots()
    {
        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var history = new RecordingHistoryStore();
        var providers = new IUsageProvider[]
        {
            new StubProvider("claude", "Claude", () => Snapshot("claude", "Claude", now)),
            new StubProvider("codex", "Codex", () => throw new InvalidOperationException("private backend body"))
        };
        var coordinator = new UsageCoordinator(providers, history, new FixedTimeProvider(now));

        var results = await coordinator.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal(SnapshotStatus.Available, results.Single(result => result.ProviderId == "claude").Status);
        var failed = results.Single(result => result.ProviderId == "codex");
        Assert.Equal(SnapshotStatus.Error, failed.Status);
        Assert.Equal("Codex failed to refresh. Try again shortly.", failed.Diagnostic);
        Assert.DoesNotContain("private", failed.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Single(history.Snapshots);
        Assert.Equal("claude", history.Snapshots[0].ProviderId);
    }

    private static ProviderSnapshot Snapshot(string id, string name, DateTimeOffset observedAt) => new(
        id,
        name,
        observedAt,
        SnapshotSource.Live,
        SnapshotStatus.Available,
        [new UsageWindow("weekly", "Weekly", 20, TimeSpan.FromDays(7), observedAt.AddDays(3))]);

    private sealed class StubProvider(string id, string displayName, Func<ProviderSnapshot> factory) : IUsageProvider
    {
        public string Id => id;
        public string DisplayName => displayName;

        public Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(factory());
    }

    private sealed class RecordingHistoryStore : IUsageHistoryStore
    {
        public List<ProviderSnapshot> Snapshots { get; } = [];

        public Task AppendAsync(ProviderSnapshot snapshot, CancellationToken cancellationToken)
        {
            Snapshots.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProviderSnapshot>> ReadAsync(string? providerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderSnapshot>>(Snapshots);
    }
}
