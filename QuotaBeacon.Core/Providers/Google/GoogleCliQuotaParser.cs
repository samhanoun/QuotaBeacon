using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using QuotaBeacon.Core.Models;

namespace QuotaBeacon.Core.Providers.Google;

public sealed record CliQuotaParseResult(
    IReadOnlyList<UsageWindow> Windows,
    string? Plan);

public static partial class GoogleCliQuotaParser
{
    public const int MaximumInputCharacters = 256 * 1024;
    private const int MaximumLineCharacters = 320;
    private const int MaximumWindows = 20;
    private static readonly TimeSpan MaximumRelativeReset = TimeSpan.FromDays(31);

    public static CliQuotaParseResult Parse(
        string output,
        DateTimeOffset observedAt,
        PercentMeaning defaultPercentMeaning)
    {
        if (string.IsNullOrEmpty(output))
        {
            return new CliQuotaParseResult([], null);
        }

        var bounded = output.Length <= MaximumInputCharacters
            ? output
            : output[^MaximumInputCharacters..];
        var normalized = StripTerminalControls(bounded).Replace('\r', '\n');
        var lines = normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(CollapseWhitespace)
            .Where(line => line.Length is > 0 and <= MaximumLineCharacters)
            .ToArray();
        var windows = new Dictionary<string, UsageWindow>(StringComparer.OrdinalIgnoreCase);
        string? plan = null;
        string? quotaGroup = null;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            plan = ParsePlan(line) ?? plan;
            quotaGroup = ParseQuotaGroup(line) ?? quotaGroup;

            if (!TryParseWindow(line, observedAt, defaultPercentMeaning, out var window) &&
                TryComposeQuotaBlock(lines, index, out var block, out var consumedLines))
            {
                _ = TryParseWindow(block, observedAt, defaultPercentMeaning, out window);
                index += consumedLines;
            }

            if (window is null)
            {
                continue;
            }

            if (quotaGroup is not null)
            {
                var groupedLabel = $"{quotaGroup} · {window.Label}";
                window = new UsageWindow(
                    ToKey(groupedLabel),
                    groupedLabel,
                    window.UsedPercent,
                    window.Duration,
                    window.ResetsAt);
            }

            windows[window.Label] = window;

            if (windows.Count >= MaximumWindows)
            {
                break;
            }
        }

        return new CliQuotaParseResult(
            windows.Values.OrderBy(window => window.Label, StringComparer.OrdinalIgnoreCase).ToArray(),
            plan);
    }

    public static string? SummarizeDiagnostic(string output, string providerName)
    {
        var safe = StripTerminalControls(output.Length <= MaximumInputCharacters
            ? output
            : output[^MaximumInputCharacters..]);
        if (safe.Contains("no longer supported", StringComparison.OrdinalIgnoreCase) ||
            safe.Contains("migrate to the Antigravity", StringComparison.OrdinalIgnoreCase))
        {
            return "This Gemini CLI account has moved to Antigravity. Enable Antigravity and install the official agy CLI.";
        }

        if (safe.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
            safe.Contains("not signed in", StringComparison.OrdinalIgnoreCase) ||
            safe.Contains("unauthenticated", StringComparison.OrdinalIgnoreCase) ||
            safe.Contains("authenticating", StringComparison.OrdinalIgnoreCase))
        {
            return $"Sign in with {providerName}, then refresh QuotaBeacon.";
        }

        if (safe.Contains("no quota information", StringComparison.OrdinalIgnoreCase))
        {
            return $"{providerName} did not report quota information for this account.";
        }

        return null;
    }

    private static bool TryParseWindow(
        string line,
        DateTimeOffset observedAt,
        PercentMeaning defaultPercentMeaning,
        out UsageWindow? window)
    {
        window = null;
        var percentMatch = PercentRegex().Match(line);
        if (!percentMatch.Success ||
            !double.TryParse(
                percentMatch.Groups["value"].Value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var percent) ||
            percent is < 0 or > 100)
        {
            return false;
        }

        var lower = line.ToLowerInvariant();
        var hasResetMarker = lower.Contains("reset", StringComparison.Ordinal) ||
                             lower.Contains("refresh", StringComparison.Ordinal);
        var explicitRemaining = lower.Contains("remaining", StringComparison.Ordinal) ||
                                lower.Contains(" left", StringComparison.Ordinal);
        var explicitUsed = lower.Contains(" used", StringComparison.Ordinal);
        var explicitlyAvailable = lower.Contains("quota available", StringComparison.Ordinal);
        if (!hasResetMarker && !explicitRemaining && !explicitUsed && !explicitlyAvailable)
        {
            return false;
        }

        var name = ParseName(line[..percentMatch.Index]);
        if (name is null)
        {
            return false;
        }

        var meaning = explicitRemaining || explicitlyAvailable
            ? PercentMeaning.Remaining
            : explicitUsed
                ? PercentMeaning.Used
                : defaultPercentMeaning;
        var used = meaning == PercentMeaning.Remaining ? 100 - percent : percent;
        var reset = ParseReset(line[(percentMatch.Index + percentMatch.Length)..], observedAt);
        window = new UsageWindow(
            ToKey(name),
            name,
            used,
            InferDuration(name, reset, observedAt),
            reset);
        return true;
    }

    private static bool TryComposeQuotaBlock(
        string[] lines,
        int index,
        out string block,
        out int consumedLines)
    {
        block = string.Empty;
        consumedLines = 0;
        if (index + 2 >= lines.Length || PercentRegex().IsMatch(lines[index]))
        {
            return false;
        }

        var meter = lines[index + 1];
        var percentMatch = PercentRegex().Match(meter);
        if (!percentMatch.Success)
        {
            return false;
        }

        var meterPrefix = meter[..percentMatch.Index].Trim();
        if (meterPrefix.Length == 0 ||
            !meterPrefix.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(IsProgressToken))
        {
            return false;
        }

        var details = lines[index + 2];
        var detailsLower = details.ToLowerInvariant();
        if (!detailsLower.Contains("remaining", StringComparison.Ordinal) &&
            !detailsLower.Contains("used", StringComparison.Ordinal) &&
            !detailsLower.Contains("reset", StringComparison.Ordinal) &&
            !detailsLower.Contains("refresh", StringComparison.Ordinal) &&
            !detailsLower.Contains("quota available", StringComparison.Ordinal))
        {
            return false;
        }

        var name = ParseName(lines[index]);
        if (name is null)
        {
            return false;
        }

        block = $"{name} {meter} {details}";
        consumedLines = 2;
        return true;
    }

    private static string? ParsePlan(string line)
    {
        foreach (var prefix in new[] { "Your Plan:", "Plan:", "Tier:" })
        {
            var index = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var value = line[(index + prefix.Length)..].Trim(' ', '│', '|');
            return value.Length is > 0 and <= 80 ? value : null;
        }

        return null;
    }

    private static string? ParseQuotaGroup(string line)
    {
        if (line.EndsWith("GEMINI MODELS", StringComparison.OrdinalIgnoreCase))
        {
            return "Gemini Models";
        }

        if (line.EndsWith("CLAUDE AND GPT MODELS", StringComparison.OrdinalIgnoreCase))
        {
            return "Claude and GPT Models";
        }

        return null;
    }

    private static string? ParseName(string prefix)
    {
        var tokens = prefix.Trim(' ', '>', '•', '│', '|', '┌', '└', '├')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        while (tokens.Count > 0 && IsProgressToken(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        if (tokens.Count > 1 && tokens[0] is "OK" or "WRN" or "ERR")
        {
            tokens.RemoveAt(0);
        }

        var name = string.Join(' ', tokens).Trim(' ', '-', '–', '—', ':', '·');
        if (name.Length is 0 or > 80 ||
            name.Equals("usage", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("model usage", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return name;
    }

    private static bool IsProgressToken(string token) =>
        token.Length > 0 && token.All(character => character is
            '▬' or '░' or '▒' or '▓' or '█' or '▰' or '▱' or '■' or '□' or
            '=' or '-' or '[' or ']' or '|' or '│');

    private static DateTimeOffset? ParseReset(string suffix, DateTimeOffset observedAt)
    {
        var marker = ResetMarkerRegex().Match(suffix);
        if (!marker.Success)
        {
            return null;
        }

        var value = marker.Groups["value"].Value.Trim(' ', '(', ')', '·', '|', '│', '.');
        var units = RelativeUnitRegex().Matches(value);
        if (units.Count > 0)
        {
            var duration = TimeSpan.Zero;
            foreach (Match unit in units)
            {
                if (!double.TryParse(unit.Groups["value"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                {
                    return null;
                }

                var normalizedUnit = unit.Groups["unit"].Value.ToLowerInvariant();
                var maximumAmount = normalizedUnit switch
                {
                    "d" or "day" or "days" => 31d,
                    "h" or "hr" or "hrs" or "hour" or "hours" => 31d * 24,
                    "m" or "min" or "mins" or "minute" or "minutes" => 31d * 24 * 60,
                    "s" or "sec" or "secs" or "second" or "seconds" => 31d * 24 * 60 * 60,
                    _ => 0d
                };
                if (!double.IsFinite(amount) || amount <= 0 || amount > maximumAmount)
                {
                    return null;
                }

                var component = normalizedUnit switch
                {
                    "d" or "day" or "days" => TimeSpan.FromDays(amount),
                    "h" or "hr" or "hrs" or "hour" or "hours" => TimeSpan.FromHours(amount),
                    "m" or "min" or "mins" or "minute" or "minutes" => TimeSpan.FromMinutes(amount),
                    "s" or "sec" or "secs" or "second" or "seconds" => TimeSpan.FromSeconds(amount),
                    _ => TimeSpan.Zero
                };
                if (component <= TimeSpan.Zero || duration > MaximumRelativeReset - component)
                {
                    return null;
                }

                duration += component;
            }

            if (duration > TimeSpan.Zero)
            {
                try
                {
                    return observedAt.Add(duration);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.CurrentCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var parsed) ||
            DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out parsed))
        {
            return parsed;
        }

        return null;
    }

    private static TimeSpan? InferDuration(
        string label,
        DateTimeOffset? reset,
        DateTimeOffset observedAt)
    {
        if (label.Contains("week", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("7d", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromDays(7);
        }

        if (label.Contains("5h", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("five hour", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("session", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromHours(5);
        }

        if (reset is { } resetAt)
        {
            var untilReset = resetAt - observedAt;
            if (untilReset > TimeSpan.FromDays(1))
            {
                return TimeSpan.FromDays(7);
            }

            if (untilReset > TimeSpan.Zero && untilReset <= TimeSpan.FromHours(5.1))
            {
                return TimeSpan.FromHours(5);
            }
        }

        return TimeSpan.FromDays(1);
    }

    private static string ToKey(string label)
    {
        var builder = new StringBuilder(Math.Min(label.Length, 48));
        var pendingSeparator = false;
        foreach (var character in label.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(character);
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }

            if (builder.Length >= 48)
            {
                break;
            }
        }

        return builder.Length == 0 ? "quota" : builder.ToString();
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, MaximumLineCharacters));
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
            if (builder.Length > MaximumLineCharacters)
            {
                break;
            }
        }

        return builder.ToString().Trim();
    }

    private static string StripTerminalControls(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, MaximumInputCharacters));
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\u001b')
            {
                if (index + 1 < value.Length && value[index + 1] == ']')
                {
                    index += 2;
                    while (index < value.Length && value[index] != '\a' &&
                           !(value[index] == '\u001b' && index + 1 < value.Length && value[index + 1] == '\\'))
                    {
                        index++;
                    }

                    if (index < value.Length && value[index] == '\u001b')
                    {
                        index++;
                    }
                }
                else if (index + 1 < value.Length && value[index + 1] == '[')
                {
                    index += 2;
                    while (index < value.Length && (value[index] < '@' || value[index] > '~'))
                    {
                        index++;
                    }
                }
                else
                {
                    index++;
                    while (index < value.Length && (value[index] < '@' || value[index] > '~'))
                    {
                        index++;
                    }
                }

                continue;
            }

            if (character == '\u009b')
            {
                index++;
                while (index < value.Length && (value[index] < '@' || value[index] > '~'))
                {
                    index++;
                }

                continue;
            }

            if (char.GetUnicodeCategory(character) == UnicodeCategory.Format)
            {
                builder.Append(' ');
            }
            else if (character is '\n' or '\r' or '\t' || !char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"(?<value>\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"(?i)(?:resets?|refresh(?:es)?)\s*(?::|in|on)?\s*(?<value>.+)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ResetMarkerRegex();

    [GeneratedRegex(@"(?i)(?<value>\d+(?:\.\d+)?)\s*(?<unit>d|days?|h|hrs?|hours?|m|mins?|minutes?|s|secs?|seconds?)\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex RelativeUnitRegex();
}
