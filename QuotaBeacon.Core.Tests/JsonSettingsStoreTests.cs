using QuotaBeacon.Core.Settings;

namespace QuotaBeacon.Core.Tests;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"quotabeacon-settings-{Guid.NewGuid():N}");

    [Fact]
    public async Task Store_returns_safe_defaults_for_missing_or_corrupt_files()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        var store = new JsonSettingsStore(path);

        Assert.Equal(AppSettings.Default, await store.ReadAsync(CancellationToken.None));

        await File.WriteAllTextAsync(path, "{broken", CancellationToken.None);
        Assert.Equal(AppSettings.Default, await store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Store_round_trips_and_normalizes_refresh_intervals()
    {
        Directory.CreateDirectory(_directory);
        var store = new JsonSettingsStore(Path.Combine(_directory, "settings.json"));
        var invalid = new AppSettings(
            RefreshIntervalMinutes: 7,
            StartWithWindows: true,
            MinimizeToTray: false,
            DisabledProviderIds: [" codex ", "CODEX", "", "../unsafe", "gemini"]);

        await store.WriteAsync(invalid, CancellationToken.None);
        var loaded = await store.ReadAsync(CancellationToken.None);

        Assert.Equal(3, loaded.RefreshIntervalMinutes);
        Assert.True(loaded.StartWithWindows);
        Assert.False(loaded.MinimizeToTray);
        Assert.Equal(["codex", "gemini"], loaded.DisabledProviderIds);
        Assert.False(loaded.IsProviderEnabled("CODEX"));
        Assert.True(loaded.IsProviderEnabled("claude"));
    }

    [Fact]
    public void Provider_preferences_are_case_insensitive_and_reversible()
    {
        var disabled = AppSettings.Default.WithProviderEnabled("codex", enabled: false);

        Assert.False(disabled.IsProviderEnabled("Codex"));
        Assert.True(disabled.IsProviderEnabled("claude"));

        var enabled = disabled.WithProviderEnabled("CODEX", enabled: true);
        Assert.True(enabled.IsProviderEnabled("codex"));
        Assert.NotNull(enabled.DisabledProviderIds);
        Assert.Empty(enabled.DisabledProviderIds);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
