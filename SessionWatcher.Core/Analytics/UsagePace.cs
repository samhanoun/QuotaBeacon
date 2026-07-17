using SessionWatcher.Core.Models;

namespace SessionWatcher.Core.Analytics;

public enum PaceState
{
    Unknown,
    OnPace,
    Watch,
    Critical,
    Exhausted
}

public sealed record PaceReading(PaceState State, double? ElapsedPercent, double? DeltaPercent);

public static class UsagePace
{
    private const double OnPaceTolerance = 5;
    private const double CriticalDelta = 20;

    public static PaceReading Evaluate(UsageWindow window, DateTimeOffset observedAt)
    {
        if (window.Duration is not { } duration ||
            duration <= TimeSpan.Zero ||
            window.ResetsAt is not { } resetsAt ||
            duration > resetsAt - DateTimeOffset.MinValue)
        {
            return new PaceReading(PaceState.Unknown, null, null);
        }

        var startsAt = resetsAt - duration;
        var elapsed = Math.Clamp(
            (observedAt - startsAt).TotalMilliseconds / duration.TotalMilliseconds * 100,
            0,
            100);
        var delta = window.UsedPercent - elapsed;

        var state = window.UsedPercent >= 100
            ? PaceState.Exhausted
            : delta <= OnPaceTolerance
                ? PaceState.OnPace
                : delta <= CriticalDelta
                    ? PaceState.Watch
                    : PaceState.Critical;

        return new PaceReading(state, elapsed, delta);
    }
}
