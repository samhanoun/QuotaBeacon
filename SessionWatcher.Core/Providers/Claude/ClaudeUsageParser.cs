using System.Globalization;
using System.Text;
using System.Text.Json;
using SessionWatcher.Core.Models;

namespace SessionWatcher.Core.Providers.Claude;

public static class ClaudeUsageParser
{
    private const int MaxUsageWindows = 32;
    private const int MaxProviderTextLength = 256;
    private const int MaxModelNameLength = 192;

    public static ProviderSnapshot Parse(string json, DateTimeOffset observedAt)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ProviderDataException("Claude returned an unreadable usage response.");
            }

            var windows = new List<UsageWindow>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("limits") ||
                    property.Value.ValueKind != JsonValueKind.Object ||
                    !TryReadNumber(property.Value, "utilization", out var utilization))
                {
                    continue;
                }

                AddWindow(
                    windows,
                    keys,
                    property.Name,
                    LabelFor(property.Name),
                    utilization,
                    DurationFor(property.Name),
                    ReadReset(property.Value));
            }

            AppendScopedLimits(document.RootElement, windows, keys);

            return new ProviderSnapshot(
                "claude",
                "Claude",
                observedAt,
                SnapshotSource.Live,
                SnapshotStatus.Available,
                windows);
        }
        catch (ProviderDataException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new ProviderDataException("Claude returned an unreadable usage response.");
        }
    }

    private static void AppendScopedLimits(
        JsonElement root,
        List<UsageWindow> windows,
        HashSet<string> keys)
    {
        if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var limit in limits.EnumerateArray())
        {
            if (limit.ValueKind != JsonValueKind.Object ||
                !TryReadNumber(limit, "percent", out var percent) ||
                !TryReadModelName(limit, out var modelName))
            {
                continue;
            }

            var reset = ReadReset(limit);
            var prefix = windows.FirstOrDefault(window => reset is not null && window.ResetsAt == reset)?.Key;
            if (prefix is null && limit.TryGetProperty("group", out var groupElement))
            {
                var group = groupElement.GetString() ?? string.Empty;
                EnsureProviderTextIsBounded(group);
                prefix = group.Contains("week", StringComparison.OrdinalIgnoreCase)
                    ? "seven_day"
                    : group.Contains("five", StringComparison.OrdinalIgnoreCase)
                        ? "five_hour"
                        : "limit";
            }

            prefix ??= "limit";
            var key = $"{prefix}_{Slug(modelName)}";

            AddWindow(
                windows,
                keys,
                key,
                $"{LabelFor(prefix)} \u00B7 {modelName}",
                percent,
                DurationFor(prefix),
                reset);
        }
    }

    private static void AddWindow(
        List<UsageWindow> windows,
        HashSet<string> keys,
        string key,
        string label,
        double usedPercent,
        TimeSpan? duration,
        DateTimeOffset? resetsAt)
    {
        EnsureProviderTextIsBounded(key);
        EnsureProviderTextIsBounded(label);
        if (!keys.Add(key))
        {
            return;
        }

        if (windows.Count >= MaxUsageWindows)
        {
            throw UnreadableResponse();
        }

        windows.Add(new UsageWindow(key, label, usedPercent, duration, resetsAt));
    }

    private static bool TryReadModelName(JsonElement limit, out string modelName)
    {
        modelName = string.Empty;
        if (!limit.TryGetProperty("scope", out var scope) ||
            scope.ValueKind != JsonValueKind.Object ||
            !scope.TryGetProperty("model", out var model) ||
            model.ValueKind != JsonValueKind.Object ||
            !model.TryGetProperty("display_name", out var displayName) ||
            displayName.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        modelName = displayName.GetString() ?? string.Empty;
        if (modelName.Length > MaxModelNameLength)
        {
            throw UnreadableResponse();
        }

        return !string.IsNullOrWhiteSpace(modelName);
    }

    private static bool TryReadNumber(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var number))
        {
            return false;
        }

        return number.ValueKind switch
        {
            JsonValueKind.Number => number.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(
                number.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value),
            _ => false
        };
    }

    private static DateTimeOffset? ReadReset(JsonElement element)
    {
        if (!element.TryGetProperty("resets_at", out var reset))
        {
            return null;
        }

        if (reset.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                reset.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var timestamp))
        {
            return timestamp;
        }

        if (reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out var unixSeconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    private static TimeSpan? DurationFor(string key) => key switch
    {
        var value when value.StartsWith("five_hour", StringComparison.OrdinalIgnoreCase) => TimeSpan.FromHours(5),
        var value when value.StartsWith("seven_day", StringComparison.OrdinalIgnoreCase) => TimeSpan.FromDays(7),
        var value when value.StartsWith("daily", StringComparison.OrdinalIgnoreCase) => TimeSpan.FromDays(1),
        _ => null
    };

    private static string LabelFor(string key)
    {
        if (key.Equals("five_hour", StringComparison.OrdinalIgnoreCase))
        {
            return "5-hour";
        }

        if (key.Equals("seven_day", StringComparison.OrdinalIgnoreCase))
        {
            return "Weekly";
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.Replace('_', ' '));
    }

    private static string Slug(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, MaxProviderTextLength));
        var separatorPending = false;
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separatorPending && builder.Length > 0)
                {
                    builder.Append('_');
                }

                builder.Append(character);
                separatorPending = false;
            }
            else if (builder.Length > 0)
            {
                separatorPending = true;
            }
        }

        return builder.ToString();
    }

    private static void EnsureProviderTextIsBounded(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxProviderTextLength)
        {
            throw UnreadableResponse();
        }
    }

    private static ProviderDataException UnreadableResponse() =>
        new("Claude returned an unreadable usage response.");
}
