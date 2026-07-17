using System.Drawing;
using Microsoft.UI.Dispatching;
using QuotaBeacon.Core.Alerts;
using QuotaBeacon.Core.Models;
using Forms = System.Windows.Forms;

namespace QuotaBeacon.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon? _ownedIcon;

    public TrayIconService(
        DispatcherQueue dispatcher,
        Action showWindow,
        Func<Task> refresh,
        Action exit)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        _ownedIcon = File.Exists(iconPath) ? new Icon(iconPath) : null;
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("Open Quota Beacon", null, (_, _) => dispatcher.TryEnqueue(() => showWindow()));
        _menu.Items.Add("Refresh now", null, (_, _) => dispatcher.TryEnqueue(async () => await refresh()));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => dispatcher.TryEnqueue(() => exit()));

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _ownedIcon ?? SystemIcons.Application,
            Text = "Quota Beacon · Waiting for usage",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                dispatcher.TryEnqueue(() => showWindow());
            }
        };
    }

    public void Update(IReadOnlyList<ProviderSnapshot> snapshots)
    {
        var summary = snapshots
            .Where(snapshot => snapshot.Status == SnapshotStatus.Available)
            .Select(snapshot =>
            {
                var window = snapshot.Windows
                    .OrderBy(item => item.Duration ?? TimeSpan.MaxValue)
                    .FirstOrDefault();
                return window is null ? snapshot.ProviderName : $"{snapshot.ProviderName} {window.UsedPercent:0}%";
            });
        var text = string.Join(" · ", summary);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "Quota Beacon · Usage unavailable";
        }

        _notifyIcon.Text = text.Length <= 127 ? text : text[..127];
    }

    public void ShowAlert(UsageAlert alert)
    {
        var icon = alert.Severity switch
        {
            UsageAlertSeverity.Warning => Forms.ToolTipIcon.Warning,
            UsageAlertSeverity.Critical or UsageAlertSeverity.Exhausted => Forms.ToolTipIcon.Error,
            _ => Forms.ToolTipIcon.Info
        };
        _notifyIcon.ShowBalloonTip(5000, "Quota Beacon", alert.Message, icon);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _ownedIcon?.Dispose();
    }
}
