using QuotaBeacon.Core.IO;
using QuotaBeacon.Core.Models;

namespace QuotaBeacon.Core.Providers.Codex;

public sealed class CodexSessionLogSource : ICodexUsageSource
{
    private const int DefaultMaxFiles = 16;
    private const int DefaultMaxBytesPerFile = 2 * 1024 * 1024;
    private const int DefaultMaxDiscoveryEntries = 10_000;
    private const int DefaultMaxLineChars = 256 * 1024;

    private readonly string _sessionsDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxFiles;
    private readonly int _maxBytesPerFile;
    private readonly int _maxDiscoveryEntries;
    private readonly int _maxLineChars;

    public CodexSessionLogSource(
        string? codexHome,
        TimeProvider timeProvider,
        int maxFiles = DefaultMaxFiles,
        int maxBytesPerFile = DefaultMaxBytesPerFile,
        int maxDiscoveryEntries = DefaultMaxDiscoveryEntries,
        int maxLineChars = DefaultMaxLineChars)
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
        _maxFiles = Math.Clamp(maxFiles, 1, 2_000);
        _maxBytesPerFile = Math.Clamp(maxBytesPerFile, 1024, 16 * 1024 * 1024);
        _maxDiscoveryEntries = Math.Clamp(maxDiscoveryEntries, _maxFiles, 100_000);
        _maxLineChars = Math.Clamp(maxLineChars, 128, 1024 * 1024);
    }

    public async Task<ProviderSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_sessionsDirectory))
        {
            throw MissingSnapshot();
        }

        try
        {
            var files = NewestFileSelector.FindNewest(
                _sessionsDirectory,
                ".jsonl",
                _maxFiles,
                _maxDiscoveryEntries,
                oldestFirst: true,
                cancellationToken);

            if (files.Count == 0)
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
        var skipPartialLine = false;
        if (offset > 0)
        {
            stream.Seek(offset - 1, SeekOrigin.Begin);
            skipPartialLine = stream.ReadByte() != '\n';
        }

        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);

        var lines = new List<string>();
        await foreach (var line in BoundedLineReader.ReadLinesAsync(
                           reader,
                           _maxLineChars,
                           cancellationToken))
        {
            if (skipPartialLine)
            {
                skipPartialLine = false;
                continue;
            }

            lines.Add(line);
        }

        return lines;
    }

    private static ProviderDataException MissingSnapshot() =>
        new("No local Codex quota snapshot is available yet.");
}
