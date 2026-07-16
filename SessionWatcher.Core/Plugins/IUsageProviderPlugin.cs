using SessionWatcher.Core.Providers;

namespace SessionWatcher.Core.Plugins;

public interface IUsageProviderPlugin
{
    string Id { get; }

    string DisplayName { get; }

    IReadOnlyList<IUsageProvider> CreateProviders();
}
