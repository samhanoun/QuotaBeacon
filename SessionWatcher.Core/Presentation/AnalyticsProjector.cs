using System.Globalization;
using SessionWatcher.Core.Analytics;

namespace SessionWatcher.Core.Presentation;

public sealed record ActivityBarModel(
    string DayLabel,
    string DateText,
    string TokensText,
    double BarHeight,
    bool IsToday,
    string AccessibleSummary);

public sealed record ModelShareModel(
    string Model,
    string TokensText,
    string CostText,
    double SharePercent,
    string AccessibleSummary);

public sealed record AnalyticsSummaryModel(
    string TodayTokensText,
    string ThirtyDayTokensText,
    string TodayCostText,
    string EstimatedCostText,
    string SessionCountText,
    string CacheRateText,
    string OutputTokensText,
    string ReasoningTokensText,
    string TopModelText,
    string ActiveDaysText,
    string CostDisclaimer,
    IReadOnlyList<ActivityBarModel> Activity,
    IReadOnlyList<ModelShareModel> Models)
{
    public static AnalyticsSummaryModel Empty { get; } = new(
        "0",
        "0",
        "$0.00*",
        "$0.00*",
        "0",
        "0%",
        "0",
        "0",
        "No model data",
        "0 active days",
        "* API-equivalent estimate from local metadata; not your Codex subscription bill.",
        [],
        []);
}

public static class AnalyticsProjector
{
    private const double MaximumBarHeight = 76;
    private const double MinimumBarHeight = 4;

    public static AnalyticsSummaryModel Project(CodexLocalAnalyticsSnapshot snapshot)
    {
        var activityDays = snapshot.Daily.TakeLast(14).ToArray();
        var maximumTokens = activityDays.Select(day => day.Usage.TotalTokens).DefaultIfEmpty(0).Max();
        var activity = activityDays.Select(day =>
        {
            var height = maximumTokens <= 0
                ? MinimumBarHeight
                : Math.Max(
                    MinimumBarHeight,
                    day.Usage.TotalTokens / (double)maximumTokens * MaximumBarHeight);
            var tokens = FormatTokens(day.Usage.TotalTokens);
            var isToday = day.Day == DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(snapshot.ObservedAt, TimeZoneInfo.Local).DateTime);
            return new ActivityBarModel(
                day.Day.ToString("ddd", CultureInfo.CurrentCulture),
                day.Day.ToString("MMM d", CultureInfo.CurrentCulture),
                tokens,
                height,
                isToday,
                $"{day.Day:MMMM d}: {tokens} tokens across {day.Usage.Sessions} sessions.");
        }).ToArray();

        var totalTokens = Math.Max(0, snapshot.Last30Days.TotalTokens);
        var models = snapshot.Models.Take(5).Select(model =>
        {
            var share = totalTokens == 0 ? 0 : model.Usage.TotalTokens / (double)totalTokens * 100;
            var tokens = FormatTokens(model.Usage.TotalTokens);
            var cost = FormatCost(model.Usage.EstimatedCost, model.Usage.HasCompleteCostEstimate);
            return new ModelShareModel(
                model.Model,
                tokens,
                cost,
                share,
                $"{model.Model}: {tokens} tokens, {share:0.#}% of local activity, {cost} estimated cost.");
        }).ToArray();

        var cacheRate = snapshot.Today.InputTokens <= 0
            ? 0
            : snapshot.Today.CachedInputTokens / (double)snapshot.Today.InputTokens * 100;
        var activeDays = snapshot.Daily.Count(day => day.Usage.TotalTokens > 0);

        return new AnalyticsSummaryModel(
            FormatTokens(snapshot.Today.TotalTokens),
            FormatTokens(snapshot.Last30Days.TotalTokens),
            FormatCost(snapshot.Today.EstimatedCost, snapshot.Today.HasCompleteCostEstimate),
            FormatCost(snapshot.Last30Days.EstimatedCost, snapshot.Last30Days.HasCompleteCostEstimate),
            snapshot.Last30Days.Sessions.ToString("N0", CultureInfo.CurrentCulture),
            $"{cacheRate:0}%",
            FormatTokens(snapshot.Today.OutputTokens),
            FormatTokens(snapshot.Today.ReasoningOutputTokens),
            snapshot.Models.FirstOrDefault()?.Model ?? "No model data",
            $"{activeDays} active day{(activeDays == 1 ? string.Empty : "s")}",
            "* API-equivalent estimate from local metadata; not your Codex subscription bill.",
            activity,
            models);
    }

    private static string FormatTokens(long tokens)
    {
        if (tokens >= 1_000_000_000)
        {
            return $"{tokens / 1_000_000_000d:0.#}B";
        }

        if (tokens >= 1_000_000)
        {
            return $"{tokens / 1_000_000d:0.#}M";
        }

        if (tokens >= 1_000)
        {
            return $"{tokens / 1_000d:0.#}K";
        }

        return tokens.ToString("N0", CultureInfo.CurrentCulture);
    }

    private static string FormatCost(decimal cost, bool complete) =>
        $"{(complete ? string.Empty : "~")}{cost.ToString("C2", CultureInfo.GetCultureInfo("en-US"))}*";
}
