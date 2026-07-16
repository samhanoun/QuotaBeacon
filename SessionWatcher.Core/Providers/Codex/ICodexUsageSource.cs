using SessionWatcher.Core.Models;

namespace SessionWatcher.Core.Providers.Codex;

public interface ICodexUsageSource
{
    Task<ProviderSnapshot> ReadAsync(CancellationToken cancellationToken);
}
