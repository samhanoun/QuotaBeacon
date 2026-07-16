namespace SessionWatcher.Core.Settings;

public interface ISettingsStore
{
    Task<AppSettings> ReadAsync(CancellationToken cancellationToken);

    Task WriteAsync(AppSettings settings, CancellationToken cancellationToken);
}
