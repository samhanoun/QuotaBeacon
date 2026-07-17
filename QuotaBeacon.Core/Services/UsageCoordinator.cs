using QuotaBeacon.Core.History;
using QuotaBeacon.Core.Models;
using QuotaBeacon.Core.Providers;

namespace QuotaBeacon.Core.Services;

public sealed class UsageCoordinator(
    IEnumerable<IUsageProvider> providers,
    IUsageHistoryStore historyStore,
    TimeProvider timeProvider)
{
    private readonly IReadOnlyList<IUsageProvider> _providers = providers.ToArray();

    public Task<IReadOnlyList<ProviderSnapshot>> RefreshAsync(CancellationToken cancellationToken) =>
        RefreshAsync(_providers.Select(provider => provider.Id).ToArray(), cancellationToken);

    public async Task<IReadOnlyList<ProviderSnapshot>> RefreshAsync(
        IReadOnlyCollection<string> enabledProviderIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enabledProviderIds);
        var enabled = enabledProviderIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tasks = _providers
            .Where(provider => enabled.Contains(provider.Id))
            .Select(provider => RefreshProviderAsync(provider, cancellationToken));
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
