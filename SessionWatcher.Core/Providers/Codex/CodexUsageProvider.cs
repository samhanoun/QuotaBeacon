using SessionWatcher.Core.Models;

namespace SessionWatcher.Core.Providers.Codex;

public sealed class CodexUsageProvider(
    ICodexUsageSource liveSource,
    ICodexUsageSource fallbackSource,
    TimeProvider timeProvider) : IUsageProvider
{
    public string Id => "codex";

    public string DisplayName => "Codex";

    public async Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await liveSource.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            try
            {
                var fallback = await fallbackSource.ReadAsync(cancellationToken);
                return fallback with
                {
                    ProviderId = "codex",
                    ProviderName = "Codex",
                    Source = SnapshotSource.LocalFallback,
                    Diagnostic = "Live Codex usage unavailable; showing the latest local quota snapshot."
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return new ProviderSnapshot(
                    "codex",
                    "Codex",
                    timeProvider.GetUtcNow(),
                    SnapshotSource.Live,
                    SnapshotStatus.Error,
                    [],
                    Diagnostic: "Codex usage is unavailable. Start Codex once, then refresh.");
            }
        }
    }
}
