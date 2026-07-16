using SessionWatcher.Core.Analytics;
using SessionWatcher.Core.Models;

namespace SessionWatcher.Core.Tests;

public sealed class UsageWindowTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-1, 0, 100)]
    [InlineData(42.5, 42.5, 57.5)]
    [InlineData(120, 100, 0)]
    public void Constructor_clamps_percentages(double supplied, double used, double remaining)
    {
        var window = CreateWindow(supplied, TimeSpan.FromHours(5), ObservedAt.AddHours(2.5));

        Assert.Equal(used, window.UsedPercent);
        Assert.Equal(remaining, window.RemainingPercent);
    }

    [Theory]
    [InlineData(40, 50, PaceState.OnPace)]
    [InlineData(58, 50, PaceState.Watch)]
    [InlineData(75, 50, PaceState.Critical)]
    [InlineData(100, 50, PaceState.Exhausted)]
    public void Pace_compares_usage_with_elapsed_window(double used, double elapsed, PaceState expected)
    {
        var duration = TimeSpan.FromHours(5);
        var reset = ObservedAt.AddMinutes(duration.TotalMinutes * (1 - elapsed / 100));
        var reading = UsagePace.Evaluate(CreateWindow(used, duration, reset), ObservedAt);

        Assert.Equal(expected, reading.State);
        Assert.Equal(elapsed, reading.ElapsedPercent!.Value, precision: 5);
        Assert.Equal(used - elapsed, reading.DeltaPercent!.Value, precision: 5);
    }

    [Fact]
    public void Pace_is_unknown_without_duration_or_reset()
    {
        var window = new UsageWindow("custom", "Custom", 30, null, null);

        var reading = UsagePace.Evaluate(window, ObservedAt);

        Assert.Equal(PaceState.Unknown, reading.State);
        Assert.Null(reading.ElapsedPercent);
    }

    private static UsageWindow CreateWindow(double used, TimeSpan? duration, DateTimeOffset? reset) =>
        new("five_hour", "5-hour", used, duration, reset);
}
