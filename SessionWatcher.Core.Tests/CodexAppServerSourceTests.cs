using System.Text.Json;
using SessionWatcher.Core.Models;
using SessionWatcher.Core.Providers.Codex;

namespace SessionWatcher.Core.Tests;

public sealed class CodexAppServerSourceTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Source_performs_handshake_and_reads_rate_limits()
    {
        var connection = new StubConnection(
            "{\"id\":0,\"result\":{\"userAgent\":\"Codex\"}}",
            "{\"method\":\"account/updated\",\"params\":{}}",
            "{\"id\":1,\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":33,\"windowDurationMins\":300,\"resetsAt\":1784210400},\"secondary\":null}}}");
        var source = new CodexAppServerSource(
            new StubConnectionFactory(connection),
            new FixedTimeProvider(ObservedAt),
            TimeSpan.FromSeconds(1));

        var snapshot = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(33, Assert.Single(snapshot.Windows).UsedPercent);
        Assert.True(connection.Disposed);
        Assert.Equal(3, connection.Writes.Count);
        AssertMessage(connection.Writes[0], "initialize", 0);
        AssertMessage(connection.Writes[1], "initialized", null);
        AssertMessage(connection.Writes[2], "account/rateLimits/read", 1);
    }

    [Fact]
    public async Task Source_converts_server_errors_to_safe_diagnostics()
    {
        const string sensitive = "private email and raw backend body";
        var connection = new StubConnection(
            "{\"id\":0,\"result\":{}}",
            $"{{\"id\":1,\"error\":{{\"message\":\"{sensitive}\"}}}}");
        var source = new CodexAppServerSource(
            new StubConnectionFactory(connection),
            new FixedTimeProvider(ObservedAt),
            TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<ProviderDataException>(
            () => source.ReadAsync(CancellationToken.None));

        Assert.Equal("The installed Codex app-server could not read rate limits.", exception.Message);
        Assert.DoesNotContain(sensitive, exception.Message, StringComparison.Ordinal);
    }

    private static void AssertMessage(string json, string method, int? id)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(method, document.RootElement.GetProperty("method").GetString());
        if (id is null)
        {
            Assert.False(document.RootElement.TryGetProperty("id", out _));
        }
        else
        {
            Assert.Equal(id, document.RootElement.GetProperty("id").GetInt32());
        }
    }

    private sealed class StubConnectionFactory(StubConnection connection) : ICodexAppServerConnectionFactory
    {
        public Task<ICodexAppServerConnection> StartAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ICodexAppServerConnection>(connection);
    }

    private sealed class StubConnection(params string[] responses) : ICodexAppServerConnection
    {
        private readonly Queue<string> _responses = new(responses);

        public List<string> Writes { get; } = [];

        public bool Disposed { get; private set; }

        public Task WriteLineAsync(string message, CancellationToken cancellationToken)
        {
            Writes.Add(message);
            return Task.CompletedTask;
        }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_responses.Count == 0 ? null : _responses.Dequeue());

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
