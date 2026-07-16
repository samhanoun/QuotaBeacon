using SessionWatcher.Core.Models;

namespace SessionWatcher.Core.Providers.Codex;

public sealed class CodexSessionLogSource : ICodexUsageSource
{
    private const int DefaultMaxFiles = 16;
    private const int DefaultMaxBytesPerFile = 2 * 1024 * 1024;

    private readonly string _sessionsDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxFiles;
    private readonly int _maxBytesPerFile;

    public CodexSessionLogSource(
        string? codexHome,
        TimeProvider timeProvider,
        int maxFiles = DefaultMaxFiles,
        int maxBytesPerFile = DefaultMaxBytesPerFile)
    {
        var home = codexHome;
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable("CODEX_HOME");
        }

        if (string.IsNullOrWhiteSpace(home))
        {
            home = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        _sessionsDirectory = Path.Combine(home, "sessions");
        _timeProvider = timeProvider;
        _maxFiles = Math.Max(maxFiles, 1);
        _maxBytesPerFile = Math.Max(maxBytesPerFile, 1024);
    }

    public async Task<ProviderSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_sessionsDirectory))
        {
            throw MissingSnapshot();
        }

        try
        {
            var files = Directory
                .EnumerateFiles(_sessionsDirectory, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(_maxFiles)
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToArray();

            if (files.Length == 0)
            {
                throw MissingSnapshot();
            }

            var lines = new List<string>();
            foreach (var file in files)
            {
                lines.AddRange(await ReadTailLinesAsync(file.FullName, cancellationToken));
            }

            return CodexSessionLogParser.Parse(lines, _timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw MissingSnapshot();
        }
    }

    private async Task<IReadOnlyList<string>> ReadTailLinesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 8192,
            useAsync: true);
        var offset = Math.Max(0, stream.Length - _maxBytesPerFile);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);

        if (offset > 0)
        {
            _ = await reader.ReadLineAsync(cancellationToken);
        }

        var lines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static ProviderDataException MissingSnapshot() =>
        new("No local Codex quota snapshot is available yet.");
}
