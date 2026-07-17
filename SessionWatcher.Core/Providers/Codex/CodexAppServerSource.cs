using System.Text.Json;
using SessionWatcher.Core.Models;

namespace SessionWatcher.Core.Providers.Codex;

public sealed class CodexAppServerSource(
    ICodexAppServerConnectionFactory connectionFactory,
    TimeProvider timeProvider,
    TimeSpan timeout) : ICodexUsageSource
{
    private const int InitializeId = 0;
    private const int RateLimitsId = 1;

    public async Task<ProviderSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var effectiveToken = timeoutSource.Token;

        try
        {
            await using var connection = await connectionFactory.StartAsync(effectiveToken);
            await connection.WriteLineAsync(InitializeRequest(), effectiveToken);
            _ = await ReadResponseAsync(connection, InitializeId, effectiveToken);

            await connection.WriteLineAsync("{\"method\":\"initialized\"}", effectiveToken);
            await connection.WriteLineAsync(
                "{\"method\":\"account/rateLimits/read\",\"id\":1}",
                effectiveToken);
            var response = await ReadResponseAsync(connection, RateLimitsId, effectiveToken);

            using var document = JsonDocument.Parse(response);
            if (document.RootElement.TryGetProperty("error", out _))
            {
                throw ServerError();
            }

            return CodexRateLimitParser.ParseAppServerResponse(response, timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderDataException("The installed Codex app-server did not respond in time.");
        }
        catch (JsonException)
        {
            throw ServerError();
        }
    }

    private static async Task<string> ReadResponseAsync(
        ICodexAppServerConnection connection,
        int expectedId,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < 100; index++)
        {
            var line = await connection.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw ServerError();
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.Number &&
                    id.TryGetInt32(out var value) &&
                    value == expectedId)
                {
                    if (document.RootElement.TryGetProperty("error", out _))
                    {
                        throw ServerError();
                    }

                    return line;
                }
            }
            catch (JsonException)
            {
                // Ignore non-protocol stdout and keep waiting for the requested response.
            }
        }

        throw ServerError();
    }

    private static string InitializeRequest() => JsonSerializer.Serialize(new
    {
        method = "initialize",
        id = InitializeId,
        @params = new
        {
            clientInfo = new
            {
                name = "session_watcher_windows",
                title = "QuotaBeacon for Windows",
                version = "0.1.0"
            }
        }
    });

    private static ProviderDataException ServerError() =>
        new("The installed Codex app-server could not read rate limits.");
}
