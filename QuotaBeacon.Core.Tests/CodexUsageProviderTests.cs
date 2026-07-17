using QuotaBeacon.Core.Models;
using QuotaBeacon.Core.Providers.Codex;

namespace QuotaBeacon.Core.Tests;

public sealed class CodexUsageProviderTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Provider_falls_back_to_local_snapshot_without_exposing_live_error()
    {
        const string sensitiveError = "backend response included a private email and token";
        var fallback = Snapshot(SnapshotSource.LocalFallback);
        var provider = new CodexUsageProvider(
            new StubCodexSource(() => throw new InvalidOperationException(sensitiveError)),
            new StubCodexSource(() => fallback),
            new FixedTimeProvider(ObservedAt));

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(SnapshotSource.LocalFallback, snapshot.Source);
        Assert.Equal("Live Codex usage unavailable; showing the latest local quota snapshot.", snapshot.Diagnostic);
        Assert.DoesNotContain(sensitiveError, snapshot.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_returns_safe_error_when_all_sources_fail()
    {
        var provider = new CodexUsageProvider(
            new StubCodexSource(() => throw new InvalidOperationException("secret one")),
            new StubCodexSource(() => throw new IOException("secret two")),
            new FixedTimeProvider(ObservedAt));

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Error, snapshot.Status);
        Assert.Equal("Codex usage is unavailable. Start Codex once, then refresh.", snapshot.Diagnostic);
        Assert.Empty(snapshot.Windows);
    }

    private static ProviderSnapshot Snapshot(SnapshotSource source) => new(
        "codex",
        "Codex",
        ObservedAt,
        source,
        SnapshotStatus.Available,
        [new UsageWindow("codex:300", "5-hour", 25, TimeSpan.FromHours(5), ObservedAt.AddHours(2))]);

    private sealed class StubCodexSource(Func<ProviderSnapshot> factory) : ICodexUsageSource
    {
        public Task<ProviderSnapshot> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(factory());
    }
}
