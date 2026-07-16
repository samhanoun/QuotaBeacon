namespace SessionWatcher.Core.Settings;

public sealed record AppSettings(
    int RefreshIntervalMinutes = 3,
    bool StartWithWindows = false,
    bool MinimizeToTray = true)
{
    private static readonly int[] AllowedRefreshIntervals = [1, 3, 5, 15];

    public static AppSettings Default { get; } = new();

    public AppSettings Normalize() => this with
    {
        RefreshIntervalMinutes = AllowedRefreshIntervals.Contains(RefreshIntervalMinutes)
            ? RefreshIntervalMinutes
            : Default.RefreshIntervalMinutes
    };
}
