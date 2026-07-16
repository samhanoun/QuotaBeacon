using System.Net;
using System.Net.Http.Headers;
using SessionWatcher.Core.Models;

namespace SessionWatcher.Core.Providers.Claude;

public sealed class ClaudeUsageProvider(
    IClaudeCredentialReader credentialReader,
    HttpClient httpClient,
    TimeProvider timeProvider) : IUsageProvider
{
    private static readonly Uri UsageEndpoint = new("https://api.anthropic.com/api/oauth/usage");

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
            request.Headers.UserAgent.ParseAdd("sessionwatcher-windows/0.1");

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
                    "Claude temporarily limited usage checks. SessionWatcher will retry.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ErrorSnapshot(
                    observedAt,
                    SnapshotStatus.Error,
                    "Claude usage is temporarily unavailable.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
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
