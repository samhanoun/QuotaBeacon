using System.Text.Json;
using SessionWatcher.Core.Models;

namespace SessionWatcher.Core.Providers.Codex;

public static class CodexSessionLogParser
{
    public static ProviderSnapshot Parse(IEnumerable<string> lines, DateTimeOffset observedAt)
    {
        var records = new Dictionary<string, ParsedLimit>(StringComparer.OrdinalIgnoreCase);
        var sequence = 0L;

        foreach (var line in lines)
        {
            sequence++;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!CodexRateLimitParser.TryGetObject(root, "payload", out var payload) ||
                    !string.Equals(
                        CodexRateLimitParser.ReadString(payload, "type"),
                        "token_count",
                        StringComparison.Ordinal) ||
                    !CodexRateLimitParser.TryGetObject(payload, "rate_limits", out var limits))
                {
                    continue;
                }

                var limitId = CodexRateLimitParser.ReadString(limits, "limit_id") ?? "codex";
                var limitName = CodexRateLimitParser.ReadString(limits, "limit_name");
                var planType = CodexRateLimitParser.ReadString(limits, "plan_type");
                var timestamp = ReadTimestamp(root) ?? DateTimeOffset.MinValue.AddTicks(sequence);
                var windows = CodexRateLimitParser.ReadWindows(limits, limitId, limitName);
                if (windows.Count == 0)
                {
                    continue;
                }

                if (!records.TryGetValue(limitId, out var current) || timestamp >= current.Timestamp)
                {
                    records[limitId] = new ParsedLimit(timestamp, windows, planType);
                }
            }
            catch (JsonException)
            {
                // Session files can be observed while Codex is appending a line.
            }
        }

        if (records.Count == 0)
        {
            throw new ProviderDataException("No local Codex quota snapshot is available yet.");
        }

        var ordered = records
            .OrderBy(pair => pair.Key.Equals("codex", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(pair => pair.Value.Windows)
            .ToArray();
        var selectedPlan = records.TryGetValue("codex", out var primary)
            ? primary.Plan
            : records.Values.Select(record => record.Plan).FirstOrDefault(value => value is not null);

        return new ProviderSnapshot(
            "codex",
            "Codex",
            observedAt,
            SnapshotSource.LocalFallback,
            SnapshotStatus.Available,
            ordered,
            selectedPlan);
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("timestamp", out var timestamp) &&
            timestamp.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(timestamp.GetString(), out var value))
        {
            return value;
        }

        return null;
    }

    private sealed record ParsedLimit(
        DateTimeOffset Timestamp,
        IReadOnlyList<UsageWindow> Windows,
        string? Plan);
}
