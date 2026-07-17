namespace QuotaBeacon.Core.Providers.Claude;

public interface IClaudeCredentialReader
{
    ValueTask<string?> ReadAccessTokenAsync(CancellationToken cancellationToken);
}
