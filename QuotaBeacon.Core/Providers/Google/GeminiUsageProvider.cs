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
            var diagnostic = IsFreshReadOnlySession(result.Output)
                ? "Gemini CLI did not expose account quota in this fresh read-only session. " +
                  "It only exposed per-process model statistics, and QuotaBeacon will not make a model request " +
                  "just to unlock quota data."
                : GoogleCliQuotaParser.SummarizeDiagnostic(result.Output, DisplayName);

            return new ProviderSnapshot(
                Id,
                DisplayName,
                observedAt,
                SnapshotSource.Live,
                SnapshotStatus.Unavailable,
                [],
                parsed.Plan,
                diagnostic ??
                "Gemini CLI did not expose quota data. Open Gemini and run /stats model once, then refresh.");
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

    private static bool IsFreshReadOnlySession(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var bounded = output.Length <= GoogleCliQuotaParser.MaximumInputCharacters
            ? output
            : output[^GoogleCliQuotaParser.MaximumInputCharacters..];
        return bounded.Contains(
            "No API calls have been made in this session",
            StringComparison.OrdinalIgnoreCase);
    }
}
