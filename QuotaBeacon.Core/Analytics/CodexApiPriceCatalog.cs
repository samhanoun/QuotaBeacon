namespace QuotaBeacon.Core.Analytics;

public sealed record CodexApiPrice(
    decimal InputPerMillion,
    decimal CachedInputPerMillion,
    decimal OutputPerMillion);

public static class CodexApiPriceCatalog
{
    // Standard API-equivalent prices verified against OpenAI's model catalog on
    // 2026-07-17. These estimates are not Codex subscription charges.
    private static readonly (string Prefix, CodexApiPrice Price)[] Prices =
    [
        ("gpt-5.6-sol", new CodexApiPrice(5m, 0.5m, 30m)),
        ("gpt-5.6-terra", new CodexApiPrice(2.5m, 0.25m, 15m)),
        ("gpt-5.6-luna", new CodexApiPrice(1m, 0.1m, 6m)),
        ("gpt-5.5", new CodexApiPrice(5m, 0.5m, 30m)),
        ("gpt-5.4", new CodexApiPrice(2.5m, 0.25m, 15m))
    ];

    public static CodexApiPrice? Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        if (string.Equals(model, "gpt-5.6", StringComparison.OrdinalIgnoreCase))
        {
            return Prices[0].Price;
        }

        return Prices.FirstOrDefault(entry =>
                string.Equals(model, entry.Prefix, StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith($"{entry.Prefix}-20", StringComparison.OrdinalIgnoreCase))
            .Price;
    }

    public static decimal Estimate(
        CodexApiPrice price,
        long inputTokens,
        long cachedInputTokens,
        long outputTokens)
    {
        var cached = Math.Clamp(cachedInputTokens, 0, Math.Max(0, inputTokens));
        var uncached = Math.Max(0, inputTokens - cached);
        return ((decimal)uncached * price.InputPerMillion +
                (decimal)cached * price.CachedInputPerMillion +
                (decimal)Math.Max(0, outputTokens) * price.OutputPerMillion) /
               1_000_000m;
    }
}
