using QuotaBeacon.Core.Providers;

namespace QuotaBeacon.Core.Plugins;

public interface IUsageProviderPlugin
{
    string Id { get; }

    string DisplayName { get; }

    IReadOnlyList<IUsageProvider> CreateProviders();
}
