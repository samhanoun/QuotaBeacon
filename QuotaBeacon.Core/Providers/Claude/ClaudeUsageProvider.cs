using System.Net;
using System.Net.Http.Headers;
using System.Text;
using QuotaBeacon.Core.Models;

namespace QuotaBeacon.Core.Providers.Claude;

public sealed class ClaudeUsageProvider(
    IClaudeCredentialReader credentialReader,
    HttpClient httpClient,
    TimeProvider timeProvider,
    int maxResponseBytes = 256 * 1024,
    TimeSpan? bodyReadTimeout = null) : IUsageProvider
{
    private static readonly Uri UsageEndpoint = new("https://api.anthropic.com/api/oauth/usage");
    private static readonly TimeSpan DefaultBodyReadTimeout = TimeSpan.FromSeconds(12);
    private readonly int _maxResponseBytes = Math.Clamp(maxResponseBytes, 32, 4 * 1024 * 1024);
    private readonly TimeSpan _bodyReadTimeout =
        bodyReadTimeout is { } timeout && timeout > TimeSpan.Zero
            ? timeout
            : DefaultBodyReadTimeout;

    public string Id => "claude";

    public string DisplayName => "Claude";

    public async Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var observedAt = timeProvider.GetUtcNow();
        var token = await credentialReader.ReadAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return ErrorSnapshot(
                observedAt,
                SnapshotStatus.Unavailable,
                "Sign in with Claude Code to enable usage monitoring.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.UserAgent.ParseAdd("quotabeacon-windows/0.2");

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return ErrorSnapshot(
                    observedAt,
                    SnapshotStatus.Error,
                    "Claude Code authentication expired. Run claude /login and refresh.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return ErrorSnapshot(
                    observedAt,
                    SnapshotStatus.Error,
                    "Claude temporarily limited usage checks. Quota Beacon will retry.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ErrorSnapshot(
                    observedAt,
                    SnapshotStatus.Error,
                    "Claude usage is temporarily unavailable.");
            }

            var json = await ReadBoundedBodyAsync(response.Content, cancellationToken);
            return ClaudeUsageParser.Parse(json, observedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderDataException exception)
        {
            return ErrorSnapshot(observedAt, SnapshotStatus.Error, exception.Message);
        }
        catch (HttpRequestException)
        {
            return ErrorSnapshot(
                observedAt,
                SnapshotStatus.Error,
                "Cannot reach the Claude usage service. Check your connection.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return ErrorSnapshot(observedAt, SnapshotStatus.Error, "Claude usage is temporarily unavailable.");
        }
    }

    private async Task<string> ReadBoundedBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } contentLength &&
            contentLength > _maxResponseBytes)
        {
            throw UnreadableResponse();
        }

        using var bodyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bodyCancellation.CancelAfter(_bodyReadTimeout);

        try
        {
            await using var input = await content.ReadAsStreamAsync(bodyCancellation.Token);
            using var output = new MemoryStream(Math.Min(_maxResponseBytes, 16 * 1024));
            var buffer = new byte[8 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), bodyCancellation.Token);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > _maxResponseBytes)
                {
                    throw UnreadableResponse();
                }

                await output.WriteAsync(buffer.AsMemory(0, read), bodyCancellation.Token);
            }

            return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw UnreadableResponse();
        }
    }

    private static ProviderDataException UnreadableResponse() =>
        new("Claude returned an unreadable usage response.");

    private static ProviderSnapshot ErrorSnapshot(
        DateTimeOffset observedAt,
        SnapshotStatus status,
        string diagnostic) => new(
        "claude",
        "Claude",
        observedAt,
        SnapshotSource.Live,
        status,
        [],
        Diagnostic: diagnostic);
}
