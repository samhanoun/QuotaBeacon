using SessionWatcher.Core.Models;

namespace SessionWatcher.Core.Providers;

public interface IUsageProvider
{
    string Id { get; }

    string DisplayName { get; }

    Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
