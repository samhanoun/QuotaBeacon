using SessionWatcher.Core.Alerts;
using SessionWatcher.Core.Models;

namespace SessionWatcher.Core.Tests;

public sealed class UsageAlertMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Monitor_does_not_alert_on_the_first_observation()
    {
        var monitor = new UsageAlertMonitor();

        Assert.Empty(monitor.Observe(Snapshot(85)));
    }

    [Theory]
    [InlineData(79, 80, UsageAlertSeverity.Warning)]
    [InlineData(89, 90, UsageAlertSeverity.Critical)]
    [InlineData(99, 100, UsageAlertSeverity.Exhausted)]
    public void Monitor_alerts_when_usage_crosses_a_threshold(
        double previous,
        double current,
        UsageAlertSeverity severity)
    {
        var monitor = new UsageAlertMonitor();
        _ = monitor.Observe(Snapshot(previous));

        var alert = Assert.Single(monitor.Observe(Snapshot(current)));

        Assert.Equal(severity, alert.Severity);
        Assert.Contains($"{current:0}%", alert.Message);
    }

    [Fact]
    public void Monitor_reports_a_reset_after_a_nearly_exhausted_window_refills()
    {
        var monitor = new UsageAlertMonitor();
        _ = monitor.Observe(Snapshot(95));

        var alert = Assert.Single(monitor.Observe(Snapshot(5)));

        Assert.Equal(UsageAlertSeverity.Reset, alert.Severity);
        Assert.Equal("Codex 5-hour usage reset.", alert.Message);
    }

    [Fact]
    public void Monitor_evicts_windows_that_disappear_from_a_provider_snapshot()
    {
        var monitor = new UsageAlertMonitor();
        _ = monitor.Observe(Snapshot("session", 79));
        _ = monitor.Observe(Snapshot("weekly", 10));

        var alerts = monitor.Observe(Snapshot("session", 80));

        Assert.Empty(alerts);
    }

    private static ProviderSnapshot Snapshot(double used) => Snapshot("session", used);

    private static ProviderSnapshot Snapshot(string key, double used) => new(
        "codex",
        "Codex",
        Now,
        SnapshotSource.Live,
        SnapshotStatus.Available,
        [new UsageWindow(key, "5-hour", used, TimeSpan.FromHours(5), Now.AddHours(2))]);
}
