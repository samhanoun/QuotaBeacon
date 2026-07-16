namespace SessionWatcher.Core.Analytics;

public sealed record TokenUsageTotals(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TotalTokens,
    int Sessions,
    decimal EstimatedCost,
    long CostedTokens)
{
    public static TokenUsageTotals Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    public bool HasCompleteCostEstimate => CostedTokens >= TotalTokens;

    public TokenUsageTotals Add(TokenUsageTotals other) => new(
        InputTokens + other.InputTokens,
        CachedInputTokens + other.CachedInputTokens,
        OutputTokens + other.OutputTokens,
        ReasoningOutputTokens + other.ReasoningOutputTokens,
        TotalTokens + other.TotalTokens,
        Sessions + other.Sessions,
        EstimatedCost + other.EstimatedCost,
        CostedTokens + other.CostedTokens);
}

public sealed record CodexDailyActivity(DateOnly Day, TokenUsageTotals Usage);

public sealed record CodexModelActivity(string Model, TokenUsageTotals Usage);

public sealed record CodexLocalAnalyticsSnapshot(
    DateTimeOffset ObservedAt,
    TokenUsageTotals Today,
    TokenUsageTotals Last30Days,
    IReadOnlyList<CodexDailyActivity> Daily,
    IReadOnlyList<CodexModelActivity> Models);

public interface ILocalAnalyticsReader
{
    Task<CodexLocalAnalyticsSnapshot> ReadAsync(CancellationToken cancellationToken);
}
