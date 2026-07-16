using SessionWatcher.Core.Models;
using SessionWatcher.Core.Providers.Claude;

namespace SessionWatcher.Core.Tests;

public sealed class ClaudeUsageParserTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parse_reads_standard_and_model_scoped_windows()
    {
        const string json = """
        {
          "five_hour": { "utilization": 42.5, "resets_at": "2026-07-16T15:00:00Z" },
          "seven_day": { "utilization": 65, "resets_at": "2026-07-20T09:30:00Z" },
          "limits": [
            {
              "group": "weekly_models",
              "percent": 12,
              "resets_at": "2026-07-20T09:30:00Z",
              "scope": { "model": { "display_name": "Opus" } }
            }
          ],
          "extra_usage": { "is_enabled": true }
        }
        """;

        var snapshot = ClaudeUsageParser.Parse(json, ObservedAt);

        Assert.Equal("claude", snapshot.ProviderId);
        Assert.Equal(SnapshotSource.Live, snapshot.Source);
        Assert.Equal(SnapshotStatus.Available, snapshot.Status);
        Assert.Collection(
            snapshot.Windows.OrderBy(window => window.Key),
            window =>
            {
                Assert.Equal("five_hour", window.Key);
                Assert.Equal("5-hour", window.Label);
                Assert.Equal(TimeSpan.FromHours(5), window.Duration);
                Assert.Equal(42.5, window.UsedPercent);
            },
            window =>
            {
                Assert.Equal("seven_day", window.Key);
                Assert.Equal("Weekly", window.Label);
                Assert.Equal(TimeSpan.FromDays(7), window.Duration);
            },
            window =>
            {
                Assert.Equal("seven_day_opus", window.Key);
                Assert.Equal("Weekly · Opus", window.Label);
                Assert.Equal(12, window.UsedPercent);
            });
    }

    [Fact]
    public void Parse_ignores_unrelated_objects_and_keeps_unknown_quota_windows()
    {
        const string json = """
        {
          "profile": { "name": "private" },
          "monthly_beta": { "utilization": 9, "resets_at": "not-a-date" }
        }
        """;

        var snapshot = ClaudeUsageParser.Parse(json, ObservedAt);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal("monthly_beta", window.Key);
        Assert.Equal("Monthly Beta", window.Label);
        Assert.Null(window.ResetsAt);
        Assert.Null(window.Duration);
    }

    [Fact]
    public void Parse_rejects_invalid_json_with_safe_domain_exception()
    {
        var exception = Assert.Throws<ProviderDataException>(() => ClaudeUsageParser.Parse("{broken", ObservedAt));

        Assert.Equal("Claude returned an unreadable usage response.", exception.Message);
    }
}
