using System.Globalization;
using System.Text.Json;
using SessionWatcher.Core.Models;

namespace SessionWatcher.Core.Providers.Codex;

public static class CodexRateLimitParser
{
    public static ProviderSnapshot ParseAppServerResponse(string json, DateTimeOffset observedAt)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!TryGetObject(root, "result", out var result) ||
                !TryGetObject(result, "rateLimits", out var rateLimits))
            {
                throw Unreadable();
            }

            var windows = ReadWindows(rateLimits, "codex", null);
            if (windows.Count == 0)
            {
                throw Unreadable();
            }

            return new ProviderSnapshot(
                "codex",
                "Codex",
                observedAt,
                SnapshotSource.Live,
                SnapshotStatus.Available,
                windows,
                Plan: ReadString(rateLimits, "planType"));
        }
        catch (ProviderDataException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw Unreadable();
        }
    }

    internal static List<UsageWindow> ReadWindows(JsonElement rateLimits, string limitId, string? limitName)
    {
        var windows = new List<UsageWindow>();
        AddWindow(rateLimits, "primary", limitId, limitName, windows);
        AddWindow(rateLimits, "secondary", limitId, limitName, windows);
        return windows;
    }

    private static void AddWindow(
        JsonElement limits,
        string slot,
        string limitId,
        string? limitName,
        List<UsageWindow> windows)
    {
        if (!TryGetObject(limits, slot, out var window) ||
            !TryReadDouble(window, "usedPercent", "used_percent", out var used) ||
            !TryReadInt(window, "windowDurationMins", "window_minutes", out var minutes) ||
            minutes <= 0)
        {
            return;
        }

        var baseLabel = WindowLabel(minutes);
        var label = limitId.Equals("codex", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(limitName)
            ? baseLabel
            : $"{limitName} · {baseLabel}";
        var reset = TryReadLong(window, "resetsAt", "resets_at", out var unixSeconds)
            ? SafeUnixTime(unixSeconds)
            : null;

        windows.RemoveAll(item => item.Key == $"{limitId}:{minutes}");
        windows.Add(new UsageWindow(
            $"{limitId}:{minutes}",
            label,
            used,
            TimeSpan.FromMinutes(minutes),
            reset));
    }

    internal static string WindowLabel(int minutes) => minutes switch
    {
        300 => "5-hour",
        10080 => "Weekly",
        1440 => "Daily",
        _ => $"{minutes:N0}-minute"
    };

    internal static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        value = property;
        return true;
    }

    internal static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static bool TryReadDouble(
        JsonElement element,
        string camelName,
        string snakeName,
        out double value)
    {
        value = 0;
        return TryGetProperty(element, camelName, snakeName, out var property) &&
               (property.ValueKind == JsonValueKind.Number
                   ? property.TryGetDouble(out value)
                   : property.ValueKind == JsonValueKind.String &&
                     double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value));
    }

    private static bool TryReadInt(
        JsonElement element,
        string camelName,
        string snakeName,
        out int value)
    {
        value = 0;
        return TryGetProperty(element, camelName, snakeName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    private static bool TryReadLong(
        JsonElement element,
        string camelName,
        string snakeName,
        out long value)
    {
        value = 0;
        return TryGetProperty(element, camelName, snakeName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out value);
    }

    private static bool TryGetProperty(
        JsonElement element,
        string camelName,
        string snakeName,
        out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object &&
               (element.TryGetProperty(camelName, out value) || element.TryGetProperty(snakeName, out value));
    }

    private static DateTimeOffset? SafeUnixTime(long unixSeconds)
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

    private static ProviderDataException Unreadable() =>
        new("Codex returned an unreadable rate-limit response.");
}
