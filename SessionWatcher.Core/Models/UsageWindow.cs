namespace SessionWatcher.Core.Models;

public sealed record UsageWindow
{
    public UsageWindow(
        string key,
        string label,
        double usedPercent,
        TimeSpan? duration,
        DateTimeOffset? resetsAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        Key = key;
        Label = label;
        UsedPercent = Math.Clamp(usedPercent, 0, 100);
        Duration = duration;
        ResetsAt = resetsAt;
    }

    public string Key { get; }

    public string Label { get; }

    public double UsedPercent { get; }

    public double RemainingPercent => 100 - UsedPercent;

    public TimeSpan? Duration { get; }

    public DateTimeOffset? ResetsAt { get; }
}
