using QuotaBeacon.Core.Models;

namespace QuotaBeacon.Core.Providers.Codex;

public interface ICodexUsageSource
{
    Task<ProviderSnapshot> ReadAsync(CancellationToken cancellationToken);
}
