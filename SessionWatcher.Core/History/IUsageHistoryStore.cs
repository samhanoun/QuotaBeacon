using SessionWatcher.Core.Models;

namespace SessionWatcher.Core.History;

public interface IUsageHistoryStore
{
    Task AppendAsync(ProviderSnapshot snapshot, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderSnapshot>> ReadAsync(
        string? providerId,
        CancellationToken cancellationToken);
}
