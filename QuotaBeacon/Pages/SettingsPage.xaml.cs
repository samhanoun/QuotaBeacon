using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using QuotaBeacon.Core.Settings;
using QuotaBeacon.Services;

namespace QuotaBeacon.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly Dictionary<string, ToggleSwitch> _providerToggles =
        new(StringComparer.OrdinalIgnoreCase);

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
            .First(item => item.Tag?.ToString() ==
                           settings.RefreshIntervalMinutes.ToString(CultureInfo.InvariantCulture));
        StartWithWindowsToggle.IsOn = settings.StartWithWindows;
        MinimizeToTrayToggle.IsOn = settings.MinimizeToTray;
        BuildProviderToggles(settings);
    }

    private void BuildProviderToggles(AppSettings settings)
    {
        ProviderTogglesPanel.Children.Clear();
        _providerToggles.Clear();
        foreach (var provider in App.Current.Runtime.Providers)
        {
            var toggle = new ToggleSwitch
            {
                Header = provider.DisplayName,
                IsOn = settings.IsProviderEnabled(provider.Id),
                OnContent = "Shown and refreshed",
                OffContent = "Hidden",
                Tag = provider.Id
            };
            AutomationProperties.SetName(toggle, $"Enable {provider.DisplayName}");
            _providerToggles.Add(provider.Id, toggle);
            ProviderTogglesPanel.Children.Add(toggle);
        }
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs args)
    {
        SaveStatus.IsOpen = false;
        try
        {
            var selected = (ComboBoxItem)RefreshIntervalCombo.SelectedItem;
            var interval = int.Parse(selected.Tag!.ToString()!, CultureInfo.InvariantCulture);
            StartupRegistrationService.SetEnabled(StartWithWindowsToggle.IsOn);
            var settings = App.Current.Runtime.CurrentSettings with
            {
                RefreshIntervalMinutes = interval,
                StartWithWindows = StartWithWindowsToggle.IsOn,
                MinimizeToTray = MinimizeToTrayToggle.IsOn
            };
            foreach (var (providerId, toggle) in _providerToggles)
            {
                settings = settings.WithProviderEnabled(providerId, toggle.IsOn);
            }

            await App.Current.Runtime.SaveSettingsAsync(settings, CancellationToken.None);

            SaveStatus.Title = "Settings saved";
            var enabledCount = App.Current.Runtime.EnabledProviderIds.Count;
            SaveStatus.Message = enabledCount == 0
                ? "All providers are hidden. You can re-enable them here at any time."
                : $"{enabledCount} provider{(enabledCount == 1 ? string.Empty : "s")} will appear on the overview.";
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
