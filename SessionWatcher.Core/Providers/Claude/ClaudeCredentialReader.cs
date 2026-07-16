using System.Text.Json;

namespace SessionWatcher.Core.Providers.Claude;

public sealed class ClaudeCredentialReader : IClaudeCredentialReader
{
    private readonly string _credentialsPath;

    public ClaudeCredentialReader(string? configDirectory = null)
    {
        var directory = configDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude");
        }

        _credentialsPath = Path.Combine(directory, ".credentials.json");
    }

    public async ValueTask<string?> ReadAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_credentialsPath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                _credentialsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            return document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) &&
                   oauth.ValueKind == JsonValueKind.Object &&
                   oauth.TryGetProperty("accessToken", out var token) &&
                   token.ValueKind == JsonValueKind.String
                ? token.GetString()
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
