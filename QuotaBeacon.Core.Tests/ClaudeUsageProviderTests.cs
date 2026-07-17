using System.Net;
using System.Net.Http.Headers;
using System.Text;
using QuotaBeacon.Core.Models;
using QuotaBeacon.Core.Providers.Claude;

namespace QuotaBeacon.Core.Tests;

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

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "Claude temporarily limited usage checks. QuotaBeacon will retry.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Claude usage is temporarily unavailable.")]
    public async Task Provider_reports_safe_service_errors(HttpStatusCode statusCode, string expectedDiagnostic)
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(statusCode));

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Error, snapshot.Status);
        Assert.Equal(expectedDiagnostic, snapshot.Diagnostic);
    }

    [Fact]
    public async Task Provider_reports_malformed_success_responses_without_exposing_payloads()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json-sensitive-payload")
        });

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Error, snapshot.Status);
        Assert.DoesNotContain("sensitive-payload", snapshot.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "Cannot reach the Claude usage service. Check your connection.")]
    [InlineData(false, "Claude usage is temporarily unavailable.")]
    public async Task Provider_maps_transport_failures_to_actionable_safe_errors(
        bool networkFailure,
        string expectedDiagnostic)
    {
        var provider = CreateProvider(_ =>
        {
            if (networkFailure)
            {
                throw new HttpRequestException("network details");
            }

            throw new InvalidOperationException("handler details");
        });

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Error, snapshot.Status);
        Assert.Equal(expectedDiagnostic, snapshot.Diagnostic);
    }

    [Fact]
    public async Task Provider_preserves_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = CreateProvider(_ => throw new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetSnapshotAsync(cancellation.Token));
    }

    [Theory]
    [InlineData(64, SnapshotStatus.Available)]
    [InlineData(65, SnapshotStatus.Error)]
    public async Task Provider_enforces_the_decoded_response_budget(
        int responseBytes,
        SnapshotStatus expectedStatus)
    {
        const int maxResponseBytes = 64;
        const string json = """{"five_hour":{"utilization":25}}""";
        var body = json.PadRight(responseBytes, ' ');
        var provider = new ClaudeUsageProvider(
            new StubClaudeCredentialReader("test-token"),
            new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)))
            })),
            TimeProvider.System,
            maxResponseBytes,
            TimeSpan.FromSeconds(1));

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(expectedStatus, snapshot.Status);
        if (expectedStatus == SnapshotStatus.Error)
        {
            Assert.Equal("Claude returned an unreadable usage response.", snapshot.Diagnostic);
        }
    }

    [Fact]
    public async Task Provider_times_out_a_body_that_stalls_after_headers()
    {
        var provider = new ClaudeUsageProvider(
            new StubClaudeCredentialReader("test-token"),
            new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream())
            })),
            TimeProvider.System,
            maxResponseBytes: 1024,
            bodyReadTimeout: TimeSpan.FromMilliseconds(50));

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Error, snapshot.Status);
        Assert.Equal("Claude returned an unreadable usage response.", snapshot.Diagnostic);
    }

    private static ClaudeUsageProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) =>
        new(
            new StubClaudeCredentialReader("test-token"),
            new HttpClient(new StubHttpHandler(responseFactory)),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero)));

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

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
