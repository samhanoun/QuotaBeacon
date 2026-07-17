using System.Text.Json;
using QuotaBeacon.Core.IO;

namespace QuotaBeacon.Core.Analytics;

public sealed class CodexLocalAnalyticsReader : ILocalAnalyticsReader
{
    private const int DefaultDays = 30;
    private const int DefaultMaxFiles = 256;
    private const int DefaultMaxDiscoveryEntries = 10_000;
    private const int DefaultMaxBytesPerFile = 4 * 1024 * 1024;
    private const int DefaultMaxTotalBytes = 64 * 1024 * 1024;
    private const int DefaultMaxLineChars = 256 * 1024;

    private readonly string _sessionsDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly int _days;
    private readonly int _maxFiles;
    private readonly int _maxDiscoveryEntries;
    private readonly int _maxBytesPerFile;
    private readonly int _maxTotalBytes;
    private readonly int _maxLineChars;

    public CodexLocalAnalyticsReader(
        string? codexHome,
        TimeProvider timeProvider,
        int days = DefaultDays,
        int maxFiles = DefaultMaxFiles,
        int maxDiscoveryEntries = DefaultMaxDiscoveryEntries,
        int maxBytesPerFile = DefaultMaxBytesPerFile,
        int maxTotalBytes = DefaultMaxTotalBytes,
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
        _days = Math.Clamp(days, 1, 90);
        _maxFiles = Math.Clamp(maxFiles, 1, 2_000);
        _maxDiscoveryEntries = Math.Clamp(maxDiscoveryEntries, _maxFiles, 100_000);
        _maxBytesPerFile = Math.Clamp(maxBytesPerFile, 1024, 16 * 1024 * 1024);
        _maxTotalBytes = Math.Clamp(maxTotalBytes, _maxBytesPerFile, 256 * 1024 * 1024);
        _maxLineChars = Math.Clamp(maxLineChars, 128, 1024 * 1024);
    }

    public async Task<CodexLocalAnalyticsSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var observedAt = _timeProvider.GetUtcNow();
        var today = ToLocalDay(observedAt);
        var firstDay = today.AddDays(-(_days - 1));
        var daily = Enumerable.Range(0, _days)
            .ToDictionary(offset => firstDay.AddDays(offset), _ => new UsageAccumulator());
        var models = new Dictionary<string, UsageAccumulator>(StringComparer.OrdinalIgnoreCase);
        var total = new UsageAccumulator();

        if (!Directory.Exists(_sessionsDirectory))
        {
            return CreateSnapshot(observedAt, today, daily, models, total);
        }

        IReadOnlyList<FileInfo> files;
        try
        {
            files = NewestFileSelector.FindNewest(
                _sessionsDirectory,
                ".jsonl",
                _maxFiles,
                _maxDiscoveryEntries,
                oldestFirst: false,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return CreateSnapshot(observedAt, today, daily, models, total);
        }

        var remainingBytes = (long)_maxTotalBytes;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remainingBytes <= 0)
            {
                break;
            }

            var fileBudget = (int)Math.Min(remainingBytes, _maxBytesPerFile);
            remainingBytes -= await ReadFileAsync(
                file.FullName,
                firstDay,
                today,
                daily,
                models,
                total,
                fileBudget,
                cancellationToken);
        }

        return CreateSnapshot(observedAt, today, daily, models, total);
    }

    private async Task<long> ReadFileAsync(
        string path,
        DateOnly firstDay,
        DateOnly lastDay,
        Dictionary<DateOnly, UsageAccumulator> daily,
        Dictionary<string, UsageAccumulator> models,
        UsageAccumulator total,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        long consumedBytes = 0;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16_384,
                useAsync: true);
            consumedBytes = Math.Min(stream.Length, maxBytes);
            var offset = Math.Max(0, stream.Length - consumedBytes);
            var skipPartialLine = false;
            if (offset > 0)
            {
                stream.Seek(offset - 1, SeekOrigin.Begin);
                skipPartialLine = stream.ReadByte() != '\n';
            }

            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var currentModel = "Unknown model";

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

                if (!line.Contains("token_count", StringComparison.Ordinal) &&
                    !line.Contains("turn_context", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!TryGetObject(root, "payload", out var payload))
                    {
                        continue;
                    }

                    var outerType = ReadString(root, "type");
                    if (string.Equals(outerType, "turn_context", StringComparison.Ordinal))
                    {
                        currentModel = ReadString(payload, "model") ?? currentModel;
                        continue;
                    }

                    if (!string.Equals(outerType, "event_msg", StringComparison.Ordinal) ||
                        !string.Equals(ReadString(payload, "type"), "token_count", StringComparison.Ordinal) ||
                        !TryGetObject(payload, "info", out var info) ||
                        !TryGetObject(info, "last_token_usage", out var usage) ||
                        !ReadTimestamp(root, out var timestamp))
                    {
                        continue;
                    }

                    var day = ToLocalDay(timestamp);
                    if (day < firstDay || day > lastDay || !daily.TryGetValue(day, out var dayAccumulator))
                    {
                        continue;
                    }

                    var input = ReadLong(usage, "input_tokens");
                    var cached = ReadLong(usage, "cached_input_tokens");
                    var output = ReadLong(usage, "output_tokens");
                    var reasoning = ReadLong(usage, "reasoning_output_tokens");
                    var tokenTotal = ReadLong(usage, "total_tokens");
                    if (tokenTotal <= 0)
                    {
                        tokenTotal = Math.Max(0, input) + Math.Max(0, output);
                    }

                    if (tokenTotal <= 0)
                    {
                        continue;
                    }

                    var price = CodexApiPriceCatalog.Find(currentModel);
                    var estimatedCost = price is null
                        ? 0m
                        : CodexApiPriceCatalog.Estimate(price, input, cached, output);
                    var costedTokens = price is null ? 0 : tokenTotal;

                    dayAccumulator.Add(
                        path,
                        input,
                        cached,
                        output,
                        reasoning,
                        tokenTotal,
                        estimatedCost,
                        costedTokens);
                    total.Add(
                        path,
                        input,
                        cached,
                        output,
                        reasoning,
                        tokenTotal,
                        estimatedCost,
                        costedTokens);
                    if (!models.TryGetValue(currentModel, out var modelAccumulator))
                    {
                        modelAccumulator = new UsageAccumulator();
                        models[currentModel] = modelAccumulator;
                    }

                    modelAccumulator.Add(
                        path,
                        input,
                        cached,
                        output,
                        reasoning,
                        tokenTotal,
                        estimatedCost,
                        costedTokens);
                }
                catch (JsonException)
                {
                    // A live session can end with a partially written JSON line.
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // One unreadable session must not hide analytics from all other sessions.
        }

        return consumedBytes;
    }

    private static CodexLocalAnalyticsSnapshot CreateSnapshot(
        DateTimeOffset observedAt,
        DateOnly today,
        IReadOnlyDictionary<DateOnly, UsageAccumulator> daily,
        IReadOnlyDictionary<string, UsageAccumulator> models,
        UsageAccumulator total)
    {
        var projectedDays = daily
            .OrderBy(pair => pair.Key)
            .Select(pair => new CodexDailyActivity(pair.Key, pair.Value.ToTotals()))
            .ToArray();
        var projectedModels = models
            .Select(pair => new CodexModelActivity(pair.Key, pair.Value.ToTotals()))
            .OrderByDescending(model => model.Usage.TotalTokens)
            .ThenBy(model => model.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CodexLocalAnalyticsSnapshot(
            observedAt,
            daily.TryGetValue(today, out var todayUsage) ? todayUsage.ToTotals() : TokenUsageTotals.Empty,
            total.ToTotals(),
            projectedDays,
            projectedModels);
    }

    private DateOnly ToLocalDay(DateTimeOffset timestamp) => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(timestamp, _timeProvider.LocalTimeZone).DateTime);

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number)
            ? Math.Max(0, number)
            : 0;

    private static bool ReadTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        if (root.TryGetProperty("timestamp", out var value) &&
            value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), out timestamp))
        {
            return true;
        }

        timestamp = default;
        return false;
    }

    private sealed class UsageAccumulator
    {
        private readonly HashSet<string> _sessions = new(StringComparer.OrdinalIgnoreCase);

        public long InputTokens { get; private set; }

        public long CachedInputTokens { get; private set; }

        public long OutputTokens { get; private set; }

        public long ReasoningOutputTokens { get; private set; }

        public long TotalTokens { get; private set; }

        public decimal EstimatedCost { get; private set; }

        public long CostedTokens { get; private set; }

        public void Add(
            string session,
            long inputTokens,
            long cachedInputTokens,
            long outputTokens,
            long reasoningOutputTokens,
            long totalTokens,
            decimal estimatedCost,
            long costedTokens)
        {
            _sessions.Add(session);
            InputTokens += inputTokens;
            CachedInputTokens += cachedInputTokens;
            OutputTokens += outputTokens;
            ReasoningOutputTokens += reasoningOutputTokens;
            TotalTokens += totalTokens;
            EstimatedCost += estimatedCost;
            CostedTokens += costedTokens;
        }

        public TokenUsageTotals ToTotals() => new(
            InputTokens,
            CachedInputTokens,
            OutputTokens,
            ReasoningOutputTokens,
            TotalTokens,
            _sessions.Count,
            EstimatedCost,
            CostedTokens);
    }
}
