using QuotaBeacon.Core.Models;

namespace QuotaBeacon.Core.Providers.Google;

public sealed class GeminiUsageProvider(
    ICliUsageSource usageSource,
    TimeProvider timeProvider) : IUsageProvider
{
    public string Id => "gemini";

    public string DisplayName => "Gemini CLI";

    public async Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var observedAt = timeProvider.GetUtcNow();
        var result = await usageSource.ReadAsync(cancellationToken);
        if (result.Status != CliUsageReadStatus.Available)
        {
            return Unavailable(observedAt, result);
        }

        var parsed = GoogleCliQuotaParser.Parse(result.Output, observedAt, PercentMeaning.Used);
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
                "Gemini CLI did not expose quota data. Open Gemini and run /model once, then refresh.");
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
        Diagnostic: result.Diagnostic ?? "Gemini CLI usage is unavailable.");
}
