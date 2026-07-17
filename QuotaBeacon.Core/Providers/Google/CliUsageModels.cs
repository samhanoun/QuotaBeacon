namespace QuotaBeacon.Core.Providers.Google;

public enum CliUsageReadStatus
{
    Available,
    NotInstalled,
    Failed
}

public sealed record CliUsageReadResult(
    CliUsageReadStatus Status,
    string Output,
    string? Diagnostic);

public interface ICliUsageSource
{
    Task<CliUsageReadResult> ReadAsync(CancellationToken cancellationToken);
}

public enum PercentMeaning
{
    Used,
    Remaining
}
