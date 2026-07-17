using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SessionWatcher.Pages;

public sealed partial class PluginsPage : Page
{
    public PluginsPage()
    {
        InitializeComponent();
    }

    // These properties stay instance-based because WinUI XAML resolves them from the page data context.
#pragma warning disable CA1822
    public string PluginDirectory => App.Current.Runtime.PluginDirectory;

    public string ProviderSummary => string.Join(
        "  ·  ",
        App.Current.Runtime.Catalog.Providers.Select(provider => provider.DisplayName));

    public bool HasIssues => App.Current.Runtime.Catalog.Issues.Count > 0;

    public string IssueSummary => string.Join(
        Environment.NewLine,
        App.Current.Runtime.Catalog.Issues.Select(issue => $"{issue.FileName}: {issue.Message}"));

    public string ExternalPluginSummary => App.Current.Runtime.Catalog.Plugins.Count == 0
        ? "No external plugins loaded. Claude and Codex are built in."
        : string.Join(
            Environment.NewLine,
            App.Current.Runtime.Catalog.Plugins.Select(plugin => $"{plugin.DisplayName} ({plugin.FileName})"));
#pragma warning restore CA1822

    private void OnOpenFolderClicked(object sender, RoutedEventArgs args)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true,
            ArgumentList = { PluginDirectory }
        });
    }
}
