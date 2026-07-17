using Microsoft.Win32;

namespace QuotaBeacon.Services;

public static class StartupRegistrationService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuotaBeacon";
    private const string LegacyValueName = "SessionWatcher";
    private const string SpacedLegacyValueName = "Quota\u0020Beacon";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ??
                        Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            var executable = Environment.ProcessPath ??
                             throw new InvalidOperationException("The application path is unavailable.");
            key.SetValue(ValueName, $"\"{executable}\"");
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            key.DeleteValue(SpacedLegacyValueName, throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            key.DeleteValue(SpacedLegacyValueName, throwOnMissingValue: false);
        }
    }
}
