using System.Globalization;
using SessionWatcher.Core.Analytics;
using SessionWatcher.Core.Models;

namespace SessionWatcher.Core.Presentation;

public sealed record QuotaCardModel(
    string Key,
    string Label,
    double UsedPercent,
    string UsedText,
    string RemainingText,
    string ResetText,
    string PaceText,
    PaceState PaceState,
    string AccessibleSummary);

public sealed record ProviderCardModel(
    string ProviderId,
    string ProviderName,
    string PlanText,
    string FreshnessText,
    SnapshotStatus Status,
    IReadOnlyList<QuotaCardModel> Quotas,
    string? Diagnostic)
{
    public bool HasQuotas => Quotas.Count > 0;

    public bool HasDiagnostic => !string.IsNullOrWhiteSpace(Diagnostic);
}

public static class DashboardProjector
{
    public static ProviderCardModel Project(ProviderSnapshot snapshot, DateTimeOffset now)
    {
        var quotas = snapshot.Windows
            .OrderBy(window => window.Duration ?? TimeSpan.MaxValue)
            .ThenBy(window => window.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(window => ProjectQuota(snapshot.ProviderName, window, now))
            .ToArray();

        return new ProviderCardModel(
            snapshot.ProviderId,
            snapshot.ProviderName,
            TitleCase(snapshot.Plan),
            $"{SourceText(snapshot.Source)} · {AgeText(snapshot.ObservedAt, now)}",
            snapshot.Status,
            quotas,
            snapshot.Diagnostic);
    }

    private static QuotaCardModel ProjectQuota(
        string providerName,
        UsageWindow window,
        DateTimeOffset now)
    {
        var used = $"{window.UsedPercent:0}% used";
        var remaining = $"{window.RemainingPercent:0}% left";
        var reset = ResetText(window.ResetsAt, now);
        var pace = UsagePace.Evaluate(window, now);
        var paceText = pace.State switch
        {
            PaceState.OnPace => "On pace",
            PaceState.Watch => "Watch pace",
            PaceState.Critical => "Over pace",
            PaceState.Exhausted => "Limit reached",
            _ => "Pace unavailable"
        };

        return new QuotaCardModel(
            window.Key,
            window.Label,
            window.UsedPercent,
            used,
            remaining,
            reset,
            paceText,
            pace.State,
            $"{providerName} {window.Label}: {used}, {remaining}, {reset}, {paceText}.");
    }

    private static string ResetText(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is null)
        {
            return "Reset time unavailable";
        }

        var remaining = resetsAt.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "Reset due";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"Resets in {(int)remaining.TotalDays}d {remaining.Hours}h";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"Resets in {(int)remaining.TotalHours}h {remaining.Minutes}m";
        }

        return $"Resets in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
    }

    private static string SourceText(SnapshotSource source) => source switch
    {
        SnapshotSource.Live => "Live",
        SnapshotSource.LocalFallback => "Local fallback",
        SnapshotSource.Cache => "Cached",
        _ => "Unknown source"
    };

    private static string AgeText(DateTimeOffset observedAt, DateTimeOffset now)
    {
        var age = now - observedAt;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "Updated just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"Updated {Math.Max(1, (int)age.TotalMinutes)}m ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"Updated {(int)age.TotalHours}h ago";
        }

        return $"Updated {(int)age.TotalDays}d ago";
    }

    private static string TitleCase(string? value) => string.IsNullOrWhiteSpace(value)
        ? "Plan unavailable"
        : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' '));
}
