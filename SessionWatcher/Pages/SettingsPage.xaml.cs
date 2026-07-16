using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SessionWatcher.Core.Settings;
using SessionWatcher.Services;

namespace SessionWatcher.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        var settings = App.Current.Runtime.CurrentSettings;
        RefreshIntervalCombo.SelectedItem = RefreshIntervalCombo.Items
            .OfType<ComboBoxItem>()
            .First(item => item.Tag?.ToString() == settings.RefreshIntervalMinutes.ToString());
        StartWithWindowsToggle.IsOn = settings.StartWithWindows;
        MinimizeToTrayToggle.IsOn = settings.MinimizeToTray;
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs args)
    {
        SaveStatus.IsOpen = false;
        try
        {
            var selected = (ComboBoxItem)RefreshIntervalCombo.SelectedItem;
            var interval = int.Parse(selected.Tag!.ToString()!);
            StartupRegistrationService.SetEnabled(StartWithWindowsToggle.IsOn);
            await App.Current.Runtime.SaveSettingsAsync(
                new AppSettings(
                    interval,
                    StartWithWindowsToggle.IsOn,
                    MinimizeToTrayToggle.IsOn),
                CancellationToken.None);

            SaveStatus.Title = "Settings saved";
            SaveStatus.Message = "The new refresh interval is active.";
            SaveStatus.Severity = InfoBarSeverity.Success;
            SaveStatus.IsOpen = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SaveStatus.Title = "Settings could not be saved";
            SaveStatus.Message = "Windows did not allow the requested settings change.";
            SaveStatus.Severity = InfoBarSeverity.Error;
            SaveStatus.IsOpen = true;
        }
    }
}
