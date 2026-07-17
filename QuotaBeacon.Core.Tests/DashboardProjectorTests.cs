using QuotaBeacon.Core.Models;
using QuotaBeacon.Core.Presentation;

namespace QuotaBeacon.Core.Tests;

public sealed class DashboardProjectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Projector_orders_windows_and_explains_remaining_reset_and_pace()
    {
        var snapshot = new ProviderSnapshot(
            "codex",
            "Codex",
            Now.AddMinutes(-2),
            SnapshotSource.Live,
            SnapshotStatus.Available,
            [
                new UsageWindow("weekly", "Weekly", 60, TimeSpan.FromDays(7), Now.AddDays(3.5)),
                new UsageWindow("session", "5-hour", 30, TimeSpan.FromHours(5), Now.AddHours(2.5))
            ],
            Plan: "pro");

        var card = DashboardProjector.Project(snapshot, Now);

        Assert.Equal("Codex", card.ProviderName);
        Assert.Equal("Pro", card.PlanText);
        Assert.Equal("Live · Updated 2m ago", card.FreshnessText);
        Assert.Collection(
            card.Quotas,
            quota =>
            {
                Assert.Equal("5-hour", quota.Label);
                Assert.Equal("70% left", quota.RemainingText);
                Assert.Equal("Resets in 2h 30m", quota.ResetText);
                Assert.Equal("On pace", quota.PaceText);
                Assert.Contains("30% used", quota.AccessibleSummary);
            },
            quota => Assert.Equal("Weekly", quota.Label));
    }

    [Fact]
    public void Projector_makes_fallback_and_error_states_unambiguous()
    {
        var fallback = new ProviderSnapshot(
            "codex",
            "Codex",
            Now,
            SnapshotSource.LocalFallback,
            SnapshotStatus.Available,
            [new UsageWindow("weekly", "Weekly", 20, TimeSpan.FromDays(7), Now.AddDays(5))],
            Diagnostic: "Live Codex usage unavailable; showing the latest local quota snapshot.");
        var error = fallback with
        {
            ProviderId = "claude",
            ProviderName = "Claude",
            Source = SnapshotSource.Live,
            Status = SnapshotStatus.Error,
            Windows = [],
            Diagnostic = "Claude usage is temporarily unavailable."
        };

        var fallbackCard = DashboardProjector.Project(fallback, Now);
        var errorCard = DashboardProjector.Project(error, Now);

        Assert.Equal("Local fallback · Updated just now", fallbackCard.FreshnessText);
        Assert.True(fallbackCard.HasDiagnostic);
        Assert.Equal("Claude usage is temporarily unavailable.", errorCard.Diagnostic);
        Assert.False(errorCard.HasQuotas);
    }
}
