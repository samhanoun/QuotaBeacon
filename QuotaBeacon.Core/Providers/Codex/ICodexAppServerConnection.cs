namespace QuotaBeacon.Core.Providers.Codex;

public interface ICodexAppServerConnection : IAsyncDisposable
{
    Task WriteLineAsync(string message, CancellationToken cancellationToken);

    Task<string?> ReadLineAsync(CancellationToken cancellationToken);
}

public interface ICodexAppServerConnectionFactory
{
    Task<ICodexAppServerConnection> StartAsync(CancellationToken cancellationToken);
}
