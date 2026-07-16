using SessionWatcher.Core.Analytics;
using SessionWatcher.Core.Presentation;

namespace SessionWatcher.Core.Tests;

public sealed class AnalyticsProjectorTests
{
    [Fact]
    public void Projector_builds_compact_metrics_charts_and_model_share()
    {
        var yesterday = new TokenUsageTotals(400, 200, 100, 20, 500, 1, 0.40m, 500);
        var today = new TokenUsageTotals(1_000, 600, 250, 80, 1_250, 2, 1.23m, 1_250);
        var snapshot = new CodexLocalAnalyticsSnapshot(
            new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero),
            today,
            yesterday.Add(today),
            [
                new CodexDailyActivity(new DateOnly(2026, 7, 16), yesterday),
                new CodexDailyActivity(new DateOnly(2026, 7, 17), today)
            ],
            [
                new CodexModelActivity("gpt-5.6-sol", today),
                new CodexModelActivity("gpt-5.5", yesterday)
            ]);

        var model = AnalyticsProjector.Project(snapshot);

        Assert.Equal("1.3K", model.TodayTokensText);
        Assert.Equal("1.8K", model.ThirtyDayTokensText);
        Assert.Equal("$1.63*", model.EstimatedCostText);
        Assert.Equal("60%", model.CacheRateText);
        Assert.Equal("gpt-5.6-sol", model.TopModelText);
        Assert.Equal(76, model.Activity[^1].BarHeight);
        Assert.InRange(model.Activity[0].BarHeight, 4, 76);
        Assert.Equal(71.4, model.Models[0].SharePercent, precision: 1);
        Assert.Contains("API-equivalent", model.CostDisclaimer);
    }
}
