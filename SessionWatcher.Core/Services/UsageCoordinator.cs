using SessionWatcher.Core.History;
using SessionWatcher.Core.Models;
using SessionWatcher.Core.Providers;

namespace SessionWatcher.Core.Services;

public sealed class UsageCoordinator(
    IEnumerable<IUsageProvider> providers,
    IUsageHistoryStore historyStore,
    TimeProvider timeProvider)
{
    private readonly IReadOnlyList<IUsageProvider> _providers = providers.ToArray();

    public async Task<IReadOnlyList<ProviderSnapshot>> RefreshAsync(CancellationToken cancellationToken)
    {
        var tasks = _providers.Select(provider => RefreshProviderAsync(provider, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    private async Task<ProviderSnapshot> RefreshProviderAsync(
        IUsageProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await provider.GetSnapshotAsync(cancellationToken);
            if (snapshot.Status == SnapshotStatus.Available)
            {
                try
                {
                    await historyStore.AppendAsync(snapshot, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // A history write must not hide a current live reading.
                }
            }

            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ProviderSnapshot(
                provider.Id,
                provider.DisplayName,
                timeProvider.GetUtcNow(),
                SnapshotSource.Live,
                SnapshotStatus.Error,
                [],
                Diagnostic: $"{provider.DisplayName} failed to refresh. Try again shortly.");
        }
    }
}
