using QuotaBeacon.Core.Models;

namespace QuotaBeacon.Core.Providers.Google;

public sealed class AntigravityUsageProvider(
    ICliUsageSource usageSource,
    TimeProvider timeProvider,
    string? probeDirectory = null) : IUsageProvider
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
            var diagnostic = IsWorkspaceTrustPrompt(result.Output)
                ? CreateWorkspaceTrustDiagnostic(probeDirectory)
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

    private static bool IsWorkspaceTrustPrompt(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var bounded = output.Length <= GoogleCliQuotaParser.MaximumInputCharacters
            ? output
            : output[^GoogleCliQuotaParser.MaximumInputCharacters..];
        var hasApprovalChoice =
            (bounded.Contains("yes", StringComparison.OrdinalIgnoreCase) &&
             bounded.Contains("no", StringComparison.OrdinalIgnoreCase)) ||
            bounded.Contains("approve", StringComparison.OrdinalIgnoreCase) ||
            bounded.Contains("continue", StringComparison.OrdinalIgnoreCase);
        var hasTrustQuestion =
            bounded.Contains("Do you trust the contents of this project?", StringComparison.OrdinalIgnoreCase) ||
            bounded.Contains("trust this folder", StringComparison.OrdinalIgnoreCase) ||
            (bounded.Contains("trust", StringComparison.OrdinalIgnoreCase) &&
             (bounded.Contains("workspace", StringComparison.OrdinalIgnoreCase) ||
              bounded.Contains("working folder", StringComparison.OrdinalIgnoreCase)));
        var hasLivePermissionPrompt = bounded.Contains(
            "requires permission to read, edit, and execute files here",
            StringComparison.OrdinalIgnoreCase);
        return hasApprovalChoice && (hasTrustQuestion || hasLivePermissionPrompt);
    }

    private static string CreateWorkspaceTrustDiagnostic(string? directory)
    {
        var safeDirectory = SafeDisplayDirectory(directory);
        var location = safeDirectory is null
            ? "the dedicated Antigravity probe directory"
            : $"\"{safeDirectory}\"";
        return $"Antigravity needs one-time workspace trust approval before QuotaBeacon can read /usage. " +
               $"Open {location} in a terminal, run agy once, approve trust, then refresh QuotaBeacon.";
    }

    private static string? SafeDisplayDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) ||
            directory.Length > 512 ||
            directory.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(directory))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(directory);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
