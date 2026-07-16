namespace SessionWatcher.Core.Models;

public enum SnapshotSource
{
    Live,
    LocalFallback,
    Cache
}

public enum SnapshotStatus
{
    Available,
    Unavailable,
    Error
}

public sealed record ProviderSnapshot(
    string ProviderId,
    string ProviderName,
    DateTimeOffset ObservedAt,
    SnapshotSource Source,
    SnapshotStatus Status,
    IReadOnlyList<UsageWindow> Windows,
    string? Plan = null,
    string? Diagnostic = null);
