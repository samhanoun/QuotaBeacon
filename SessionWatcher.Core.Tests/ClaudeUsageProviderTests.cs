using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SessionWatcher.Core.Models;
using SessionWatcher.Core.Providers.Claude;

namespace SessionWatcher.Core.Tests;

public sealed class ClaudeUsageProviderTests
{
    [Fact]
    public async Task Provider_sends_token_only_to_anthropic_usage_endpoint()
    {
        const string token = "test-oauth-token";
        HttpRequestMessage? captured = null;
        var handler = new StubHttpHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"five_hour\":{\"utilization\":25,\"resets_at\":\"2026-07-16T15:00:00Z\"}}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var provider = new ClaudeUsageProvider(
            new StubClaudeCredentialReader(token),
            new HttpClient(handler),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero)));

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Available, snapshot.Status);
        Assert.NotNull(captured);
        Assert.Equal(new Uri("https://api.anthropic.com/api/oauth/usage"), captured.RequestUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", token), captured.Headers.Authorization);
        Assert.Contains("oauth-2025-04-20", captured.Headers.GetValues("anthropic-beta"));
    }

    [Fact]
    public async Task Provider_reports_missing_login_without_network_call()
    {
        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("Network must not be called."));
        var provider = new ClaudeUsageProvider(
            new StubClaudeCredentialReader(null),
            new HttpClient(handler),
            TimeProvider.System);

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Unavailable, snapshot.Status);
        Assert.Equal("Sign in with Claude Code to enable usage monitoring.", snapshot.Diagnostic);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Provider_never_exposes_token_in_authentication_errors()
    {
        const string token = "never-leak-this-token";
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("server echoed never-leak-this-token")
        });
        var provider = new ClaudeUsageProvider(
            new StubClaudeCredentialReader(token),
            new HttpClient(handler),
            TimeProvider.System);

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Error, snapshot.Status);
        Assert.Equal("Claude Code authentication expired. Run claude /login and refresh.", snapshot.Diagnostic);
        Assert.DoesNotContain(token, snapshot.Diagnostic, StringComparison.Ordinal);
    }

    private sealed class StubClaudeCredentialReader(string? token) : IClaudeCredentialReader
    {
        public ValueTask<string?> ReadAccessTokenAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(token);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responseFactory(request));
        }
    }
}
