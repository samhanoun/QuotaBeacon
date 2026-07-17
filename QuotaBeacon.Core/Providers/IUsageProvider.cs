using QuotaBeacon.Core.Models;

namespace QuotaBeacon.Core.Providers;

public interface IUsageProvider
{
    string Id { get; }

    string DisplayName { get; }

    Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
