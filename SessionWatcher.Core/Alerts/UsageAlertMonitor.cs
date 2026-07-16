using SessionWatcher.Core.Models;

namespace SessionWatcher.Core.Alerts;

public enum UsageAlertSeverity
{
    Warning,
    Critical,
    Exhausted,
    Reset
}

public sealed record UsageAlert(
    string ProviderId,
    string WindowKey,
    UsageAlertSeverity Severity,
    string Message);

public sealed class UsageAlertMonitor
{
    private readonly Dictionary<string, double> _previousUsage = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<UsageAlert> Observe(ProviderSnapshot snapshot)
    {
        if (snapshot.Status != SnapshotStatus.Available)
        {
            return [];
        }

        var alerts = new List<UsageAlert>();
        foreach (var window in snapshot.Windows)
        {
            var key = $"{snapshot.ProviderId}:{window.Key}";
            if (_previousUsage.TryGetValue(key, out var previous))
            {
                var alert = CreateAlert(snapshot, window, previous);
                if (alert is not null)
                {
                    alerts.Add(alert);
                }
            }

            _previousUsage[key] = window.UsedPercent;
        }

        return alerts;
    }

    private static UsageAlert? CreateAlert(
        ProviderSnapshot snapshot,
        UsageWindow window,
        double previous)
    {
        if (previous >= 90 && window.UsedPercent <= 10 && previous - window.UsedPercent >= 50)
        {
            return new UsageAlert(
                snapshot.ProviderId,
                window.Key,
                UsageAlertSeverity.Reset,
                $"{snapshot.ProviderName} {window.Label} usage reset.");
        }

        var threshold = new[] { 100d, 90d, 80d }
            .FirstOrDefault(value => previous < value && window.UsedPercent >= value);
        if (threshold == 0)
        {
            return null;
        }

        var severity = threshold switch
        {
            >= 100 => UsageAlertSeverity.Exhausted,
            >= 90 => UsageAlertSeverity.Critical,
            _ => UsageAlertSeverity.Warning
        };
        return new UsageAlert(
            snapshot.ProviderId,
            window.Key,
            severity,
            $"{snapshot.ProviderName} {window.Label} usage reached {window.UsedPercent:0}%.");
    }
}
