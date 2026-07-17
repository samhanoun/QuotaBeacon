using QuotaBeacon.Core.Models;

namespace QuotaBeacon.Core.Providers.Google;

public sealed class AntigravityUsageProvider(
    ICliUsageSource usageSource,
    TimeProvider timeProvider) : IUsageProvider
{
    public string Id => "antigravity";

    public string DisplayName => "Antigravity";

    public async Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var observedAt = timeProvider.GetUtcNow();
        var result = await usageSource.ReadAsync(cancellationToken);
        if (result.Status != CliUsageReadStatus.Available)
        {
            return Unavailable(observedAt, result);
        }

        var parsed = GoogleCliQuotaParser.Parse(result.Output, observedAt, PercentMeaning.Remaining);
        if (parsed.Windows.Count == 0)
        {
            return new ProviderSnapshot(
                Id,
                DisplayName,
                observedAt,
                SnapshotSource.Live,
                SnapshotStatus.Unavailable,
                [],
                parsed.Plan,
                GoogleCliQuotaParser.SummarizeDiagnostic(result.Output, DisplayName) ??
                "Antigravity did not expose quota data. Open agy and run /usage once, then refresh.");
        }

        return new ProviderSnapshot(
            Id,
            DisplayName,
            observedAt,
            SnapshotSource.Live,
            SnapshotStatus.Available,
            parsed.Windows,
            parsed.Plan);
    }

    private ProviderSnapshot Unavailable(DateTimeOffset observedAt, CliUsageReadResult result) => new(
        Id,
        DisplayName,
        observedAt,
        SnapshotSource.Live,
        result.Status == CliUsageReadStatus.NotInstalled ? SnapshotStatus.Unavailable : SnapshotStatus.Error,
        [],
        Diagnostic: result.Diagnostic ?? "Antigravity usage is unavailable.");
}
