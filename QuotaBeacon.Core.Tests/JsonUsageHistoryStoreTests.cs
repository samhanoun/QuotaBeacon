using QuotaBeacon.Core.History;
using QuotaBeacon.Core.Models;

namespace QuotaBeacon.Core.Tests;

public sealed class JsonUsageHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"quotabeacon-history-{Guid.NewGuid():N}");

    [Fact]
    public async Task Store_prunes_old_data_deduplicates_snapshots_and_omits_diagnostics()
    {
        Directory.CreateDirectory(_directory);
        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var path = Path.Combine(_directory, "history.json");
        var store = new JsonUsageHistoryStore(path, TimeSpan.FromDays(90), new FixedTimeProvider(now));
        var old = Snapshot(now.AddDays(-91), "old-secret");
        var current = Snapshot(now, "credential-never-persisted");

        await store.AppendAsync(old, CancellationToken.None);
        await store.AppendAsync(current, CancellationToken.None);
        await store.AppendAsync(current, CancellationToken.None);

        var saved = await store.ReadAsync(null, CancellationToken.None);
        var raw = await File.ReadAllTextAsync(path, CancellationToken.None);
        var only = Assert.Single(saved);
        Assert.Equal(now, only.ObservedAt);
        Assert.Null(only.Diagnostic);
        Assert.DoesNotContain("secret", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Store_can_filter_provider_history()
    {
        Directory.CreateDirectory(_directory);
        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var store = new JsonUsageHistoryStore(Path.Combine(_directory, "history.json"), TimeSpan.FromDays(90), new FixedTimeProvider(now));
        await store.AppendAsync(Snapshot(now, null), CancellationToken.None);
        await store.AppendAsync(Snapshot(now, null) with { ProviderId = "claude", ProviderName = "Claude" }, CancellationToken.None);

        var saved = await store.ReadAsync("claude", CancellationToken.None);

        Assert.Single(saved);
        Assert.Equal("claude", saved[0].ProviderId);
    }

    private static ProviderSnapshot Snapshot(DateTimeOffset observedAt, string? diagnostic) => new(
        "codex",
        "Codex",
        observedAt,
        SnapshotSource.Live,
        SnapshotStatus.Available,
        [new UsageWindow("codex:300", "5-hour", 25, TimeSpan.FromHours(5), observedAt.AddHours(2))],
        Plan: "pro",
        Diagnostic: diagnostic);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
