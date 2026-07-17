using System.Text.Json;
using SessionWatcher.Core.Models;

namespace SessionWatcher.Core.History;

// The app-lifetime semaphore may still be awaited during shutdown; disposing it would race those operations.
#pragma warning disable CA1001
public sealed class JsonUsageHistoryStore(
    string path,
    TimeSpan retention,
    TimeProvider timeProvider) : IUsageHistoryStore
#pragma warning restore CA1001
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task AppendAsync(ProviderSnapshot snapshot, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stored = await ReadStoredAsync(cancellationToken);
            var cutoff = timeProvider.GetUtcNow() - retention;
            stored.RemoveAll(item => item.ObservedAt < cutoff);

            if (!stored.Any(item =>
                    string.Equals(item.ProviderId, snapshot.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                    item.ObservedAt == snapshot.ObservedAt))
            {
                stored.Add(StoredSnapshot.From(snapshot));
            }

            stored.Sort((left, right) => left.ObservedAt.CompareTo(right.ObservedAt));
            await WriteStoredAsync(stored, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ProviderSnapshot>> ReadAsync(
        string? providerId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cutoff = timeProvider.GetUtcNow() - retention;
            return (await ReadStoredAsync(cancellationToken))
                .Where(item => item.ObservedAt >= cutoff)
                .Where(item => providerId is null ||
                               string.Equals(item.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.ObservedAt)
                .Select(item => item.ToSnapshot())
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<StoredSnapshot>> ReadStoredAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 8192,
                useAsync: true);
            return await JsonSerializer.DeserializeAsync<List<StoredSnapshot>>(
                       stream,
                       JsonOptions,
                       cancellationToken) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task WriteStoredAsync(List<StoredSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 8192,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, snapshots, JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
    private sealed record StoredSnapshot(
        string ProviderId,
        string ProviderName,
        DateTimeOffset ObservedAt,
        SnapshotSource Source,
        SnapshotStatus Status,
        IReadOnlyList<UsageWindow> Windows,
        string? Plan)
    {
        public static StoredSnapshot From(ProviderSnapshot snapshot) => new(
            snapshot.ProviderId,
            snapshot.ProviderName,
            snapshot.ObservedAt,
            snapshot.Source,
            snapshot.Status,
            snapshot.Windows,
            snapshot.Plan);

        public ProviderSnapshot ToSnapshot() => new(
            ProviderId,
            ProviderName,
            ObservedAt,
            Source,
            Status,
            Windows,
            Plan);
    }
}
