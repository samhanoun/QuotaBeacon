using QuotaBeacon.Core.Models;
using QuotaBeacon.Core.Providers.Codex;

namespace QuotaBeacon.Core.Tests;

public sealed class CodexRateLimitParserTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void App_server_parser_reads_primary_and_secondary_windows()
    {
        const string json = """
        {
          "id": 1,
          "result": {
            "rateLimits": {
              "primary": { "usedPercent": 21, "windowDurationMins": 300, "resetsAt": 1784210400 },
              "secondary": { "usedPercent": 44, "windowDurationMins": 10080, "resetsAt": 1784736000 },
              "rateLimitReachedType": null
            }
          }
        }
        """;

        var snapshot = CodexRateLimitParser.ParseAppServerResponse(json, ObservedAt);

        Assert.Equal(SnapshotSource.Live, snapshot.Source);
        Assert.Collection(
            snapshot.Windows.OrderBy(window => window.Duration),
            window =>
            {
                Assert.Equal("5-hour", window.Label);
                Assert.Equal(21, window.UsedPercent);
                Assert.Equal(TimeSpan.FromHours(5), window.Duration);
            },
            window =>
            {
                Assert.Equal("Weekly", window.Label);
                Assert.Equal(44, window.UsedPercent);
                Assert.Equal(TimeSpan.FromDays(7), window.Duration);
            });
    }

    [Fact]
    public void Session_log_parser_uses_latest_snapshot_for_each_limit_without_reading_messages()
    {
        var lines = new[]
        {
            "{\"timestamp\":\"2026-07-16T10:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":10,\"window_minutes\":300,\"resets_at\":1784210400},\"secondary\":{\"used_percent\":20,\"window_minutes\":10080,\"resets_at\":1784736000}}}}",
            "{\"timestamp\":\"2026-07-16T10:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"private prompt and source code\"}}",
            "{\"timestamp\":\"2026-07-16T11:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{\"limit_id\":\"codex\",\"plan_type\":\"pro\",\"primary\":{\"used_percent\":35,\"window_minutes\":300,\"resets_at\":1784210400},\"secondary\":{\"used_percent\":40,\"window_minutes\":10080,\"resets_at\":1784736000}}}}",
            "{\"timestamp\":\"2026-07-16T11:05:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{\"limit_id\":\"spark\",\"limit_name\":\"GPT-5.3-Codex-Spark\",\"primary\":{\"used_percent\":5,\"window_minutes\":10080,\"resets_at\":1784736000}}}}"
        };

        var snapshot = CodexSessionLogParser.Parse(lines, ObservedAt);

        Assert.Equal(SnapshotSource.LocalFallback, snapshot.Source);
        Assert.Equal("pro", snapshot.Plan);
        Assert.Equal(3, snapshot.Windows.Count);
        Assert.Equal(35, snapshot.Windows.Single(window => window.Key == "codex:300").UsedPercent);
        Assert.Equal(40, snapshot.Windows.Single(window => window.Key == "codex:10080").UsedPercent);
        Assert.Equal("GPT-5.3-Codex-Spark · Weekly", snapshot.Windows.Single(window => window.Key == "spark:10080").Label);
        Assert.DoesNotContain("private", snapshot.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parsers_reject_responses_without_quota_windows()
    {
        Assert.Throws<ProviderDataException>(() =>
            CodexRateLimitParser.ParseAppServerResponse("{\"id\":1,\"result\":{\"rateLimits\":null}}", ObservedAt));
        Assert.Throws<ProviderDataException>(() =>
            CodexSessionLogParser.Parse(["{\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\"}}"], ObservedAt));
    }
}
