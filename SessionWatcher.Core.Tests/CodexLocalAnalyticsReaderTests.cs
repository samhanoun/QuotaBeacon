using SessionWatcher.Core.Analytics;

namespace SessionWatcher.Core.Tests;

public sealed class CodexLocalAnalyticsReaderTests : IDisposable
{
    private readonly string _codexHome = Path.Combine(
        Path.GetTempPath(),
        $"sessionwatcher-analytics-{Guid.NewGuid():N}");

    [Fact]
    public async Task Reader_aggregates_daily_tokens_sessions_models_and_estimated_cost()
    {
        var sessions = Path.Combine(_codexHome, "sessions", "2026", "07", "17");
        Directory.CreateDirectory(sessions);
        await File.WriteAllLinesAsync(
            Path.Combine(sessions, "first.jsonl"),
            [
                """{"timestamp":"2026-07-17T09:00:00Z","type":"turn_context","payload":{"model":"gpt-5.6-sol","private":"must-not-surface"}}""",
                """{"timestamp":"2026-07-17T09:01:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"cached_input_tokens":60,"output_tokens":20,"reasoning_output_tokens":8,"total_tokens":120}}}}""",
                """{"timestamp":"2026-07-17T09:02:00Z","type":"response_item","payload":{"type":"message","content":"ignored-secret"}}""",
                "{malformed"
            ],
            CancellationToken.None);
        await File.WriteAllLinesAsync(
            Path.Combine(sessions, "second.jsonl"),
            [
                """{"timestamp":"2026-07-17T11:00:00Z","type":"turn_context","payload":{"model":"gpt-5.5"}}""",
                """{"timestamp":"2026-07-17T11:01:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":200,"cached_input_tokens":100,"output_tokens":50,"reasoning_output_tokens":10,"total_tokens":250}}}}"""
            ],
            CancellationToken.None);
        var reader = new CodexLocalAnalyticsReader(
            _codexHome,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 17, 20, 0, 0, TimeSpan.Zero)));

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(370, result.Today.TotalTokens);
        Assert.Equal(300, result.Today.InputTokens);
        Assert.Equal(160, result.Today.CachedInputTokens);
        Assert.Equal(70, result.Today.OutputTokens);
        Assert.Equal(2, result.Today.Sessions);
        Assert.Equal(0.00288m, result.Today.EstimatedCost);
        Assert.True(result.Today.HasCompleteCostEstimate);
        Assert.Equal(30, result.Daily.Count);
        Assert.Equal(new DateOnly(2026, 7, 17), result.Daily[^1].Day);
        Assert.Equal(2, result.Models.Count);
        Assert.Equal("gpt-5.5", result.Models[0].Model);
        Assert.Equal(250, result.Models[0].Usage.TotalTokens);
        Assert.DoesNotContain("secret", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reader_returns_a_zero_filled_window_when_sessions_are_missing()
    {
        var reader = new CodexLocalAnalyticsReader(
            _codexHome,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 17, 20, 0, 0, TimeSpan.Zero)));

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(0, result.Last30Days.TotalTokens);
        Assert.Equal(30, result.Daily.Count);
        Assert.All(result.Daily, day => Assert.Equal(0, day.Usage.TotalTokens));
    }

    [Fact]
    public async Task Reader_skips_overlong_records_within_bounded_file_reads()
    {
        var sessions = Path.Combine(_codexHome, "sessions");
        Directory.CreateDirectory(sessions);
        var oversized = """{"timestamp":"2026-07-17T09:00:00Z","type":"event_msg","payload":{"type":"token_count","padding":""" +
                        new string('x', 256) +
                        ""","info":{"last_token_usage":{"total_tokens":999}}}}""";
        var valid = """{"timestamp":"2026-07-17T09:01:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":10,"total_tokens":10}}}}""";
        await File.WriteAllLinesAsync(
            Path.Combine(sessions, "bounded.jsonl"),
            [oversized, valid],
            CancellationToken.None);
        var reader = new CodexLocalAnalyticsReader(
            _codexHome,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 17, 20, 0, 0, TimeSpan.Zero)),
            maxFiles: 4,
            maxDiscoveryEntries: 100,
            maxBytesPerFile: 1024,
            maxTotalBytes: 1024,
            maxLineChars: 192);

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(10, result.Today.TotalTokens);
    }

    [Theory]
    [InlineData("gpt-5.6-sol", 5, 0.5, 30)]
    [InlineData("gpt-5.6-terra", 2.5, 0.25, 15)]
    [InlineData("gpt-5.6-luna", 1, 0.1, 6)]
    [InlineData("gpt-5.5", 5, 0.5, 30)]
    [InlineData("gpt-5.4", 2.5, 0.25, 15)]
    public void Price_catalog_uses_official_standard_API_rates(
        string model,
        double inputRate,
        double cachedRate,
        double outputRate)
    {
        var price = CodexApiPriceCatalog.Find(model);

        Assert.NotNull(price);
        Assert.Equal((decimal)inputRate, price.InputPerMillion);
        Assert.Equal((decimal)cachedRate, price.CachedInputPerMillion);
        Assert.Equal((decimal)outputRate, price.OutputPerMillion);
    }

    [Theory]
    [InlineData("gpt-5.5-pro")]
    [InlineData("gpt-5.4-mini")]
    [InlineData("unpublished-model")]
    public void Price_catalog_does_not_apply_base_model_rates_to_other_tiers(string model)
    {
        Assert.Null(CodexApiPriceCatalog.Find(model));
    }

    public void Dispose()
    {
        if (Directory.Exists(_codexHome))
        {
            Directory.Delete(_codexHome, recursive: true);
        }
    }
}
