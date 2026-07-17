using QuotaBeacon.Core.Providers;

namespace QuotaBeacon.Core.Settings;

public sealed record AppSettings(
    int RefreshIntervalMinutes = 3,
    bool StartWithWindows = false,
    bool MinimizeToTray = true,
    IReadOnlyList<string>? DisabledProviderIds = null)
{
    private static readonly int[] AllowedRefreshIntervals = [1, 3, 5, 15];

    public static AppSettings Default { get; } = new(DisabledProviderIds: Array.Empty<string>());

    public AppSettings Normalize() => this with
    {
        RefreshIntervalMinutes = AllowedRefreshIntervals.Contains(RefreshIntervalMinutes)
            ? RefreshIntervalMinutes
            : Default.RefreshIntervalMinutes,
        DisabledProviderIds = NormalizeProviderIds(DisabledProviderIds)
    };

    public bool IsProviderEnabled(string providerId)
    {
        var normalizedId = NormalizeProviderId(providerId) ??
                           throw new ArgumentException("Provider ID is invalid.", nameof(providerId));
        return !(DisabledProviderIds ?? Array.Empty<string>()).Contains(
            normalizedId,
            StringComparer.OrdinalIgnoreCase);
    }

    public AppSettings WithProviderEnabled(string providerId, bool enabled)
    {
        var normalizedId = NormalizeProviderId(providerId) ??
                           throw new ArgumentException("Provider ID is invalid.", nameof(providerId));
        var disabled = (DisabledProviderIds ?? Array.Empty<string>())
            .Where(id => !string.Equals(id, normalizedId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!enabled)
        {
            disabled.Add(normalizedId);
        }

        return (this with { DisabledProviderIds = disabled }).Normalize();
    }

    private static string[] NormalizeProviderIds(IEnumerable<string>? providerIds) =>
        (providerIds ?? Array.Empty<string>())
        .Select(NormalizeProviderId)
        .Where(id => id is not null)
        .Select(id => id!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string? NormalizeProviderId(string? providerId)
    {
        return ProviderIdentity.TryNormalize(providerId, out var normalizedId)
            ? normalizedId
            : null;
    }
}
