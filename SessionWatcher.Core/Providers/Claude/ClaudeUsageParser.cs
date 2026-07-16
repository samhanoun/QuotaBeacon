using System.Globalization;
using System.Text.Json;
using SessionWatcher.Core.Models;

namespace SessionWatcher.Core.Providers.Claude;

public static class ClaudeUsageParser
{
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

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("limits") ||
                    property.Value.ValueKind != JsonValueKind.Object ||
                    !TryReadNumber(property.Value, "utilization", out var utilization))
                {
                    continue;
                }

                windows.Add(new UsageWindow(
                    property.Name,
                    LabelFor(property.Name),
                    utilization,
                    DurationFor(property.Name),
                    ReadReset(property.Value)));
            }

            AppendScopedLimits(document.RootElement, windows);

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

    private static void AppendScopedLimits(JsonElement root, List<UsageWindow> windows)
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
                prefix = group.Contains("week", StringComparison.OrdinalIgnoreCase)
                    ? "seven_day"
                    : group.Contains("five", StringComparison.OrdinalIgnoreCase)
                        ? "five_hour"
                        : "limit";
            }

            prefix ??= "limit";
            var key = $"{prefix}_{Slug(modelName)}";
            if (windows.Any(window => string.Equals(window.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            windows.Add(new UsageWindow(
                key,
                $"{LabelFor(prefix)} · {modelName}",
                percent,
                DurationFor(prefix),
                reset));
        }
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

    private static string Slug(string value) => string.Join(
        '_',
        value.ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .Aggregate(new List<string> { string.Empty }, (parts, character) =>
            {
                if (character == ' ')
                {
                    if (parts[^1].Length > 0)
                    {
                        parts.Add(string.Empty);
                    }
                }
                else
                {
                    parts[^1] += character;
                }

                return parts;
            })
            .Where(part => part.Length > 0));
}
