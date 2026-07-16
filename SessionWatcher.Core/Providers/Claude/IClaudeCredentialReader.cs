namespace SessionWatcher.Core.Providers.Claude;

public interface IClaudeCredentialReader
{
    ValueTask<string?> ReadAccessTokenAsync(CancellationToken cancellationToken);
}
