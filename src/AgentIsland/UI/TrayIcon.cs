using System.ComponentModel;
using AgentIsland.Core;
using AgentIsland.Model;
using AgentIsland.Usage;

namespace AgentIsland.UI;

/// System tray entry point — the Windows-native home for the island's ambient
/// signal. The icon itself visualizes status (a usage ring that turns amber /
/// red for approaching-limit and attention states), a left click pops the
/// island open, and the menu holds show/hide, settings, and quit.
public sealed class TrayIcon : IDisposable
{
    /// Set by App at startup; the reminder center routes its system
    /// notification (balloon) through here.
    public static TrayIcon? Current { get; set; }

    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private System.Drawing.Icon? _rendered;

    public TrayIcon(Action showIsland, Action toggleIsland, Action openSettings, Action exit)
    {
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Localization.L10n.Tr("Show / Hide island"), null, (_, _) => toggleIsland());
        menu.Items.Add(Localization.L10n.Tr("Share weekly report…"), null,
            (_, _) => Report.ReportWindow.Show(Report.ReportWindow.Kind.Weekly));
        menu.Items.Add(Localization.L10n.Tr("Share monthly report…"), null,
            (_, _) => Report.ReportWindow.Show(Report.ReportWindow.Kind.Monthly));
        menu.Items.Add(Localization.L10n.Tr("Settings…"), null, (_, _) => openSettings());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Localization.L10n.Tr("Quit Agent Island"), null, (_, _) => exit());

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Agent Island",
            ContextMenuStrip = menu,
        };
        // Left click pops the island up; right-click opens the menu.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) showIsland();
        };

        UsageStore.Shared.PropertyChanged += OnDataChanged;
        ActivityMonitor.Shared.PropertyChanged += OnDataChanged;
        ProviderVisibilityStore.Shared.PropertyChanged += OnDataChanged;
        Update();               // paints the first icon
        _icon.Visible = true;   // then show it
    }

    private void OnDataChanged(object? sender, PropertyChangedEventArgs e) =>
        _dispatcher.BeginInvoke(Update);

    /// Windows banner ("toast") in the tray corner — the system-notification
    /// half of an alarm, next to the foreground alarm window. On Win 10/11 a
    /// balloon tip renders as a native toast and lands in the Action Center.
    public void ShowBanner(string title, string message)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = string.IsNullOrWhiteSpace(message) ? title : message;
            _icon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.None;
            _icon.ShowBalloonTip(5000);
        }
        catch
        {
        }
    }

    private void Update()
    {
        var visibility = ProviderVisibilityStore.Shared;
        var usage = UsageStore.Shared;
        var monitor = ActivityMonitor.Shared;

        double usage5h = 0;
        var worst = ActivityState.Idle;
        string? claudeText = null, codexText = null;
        if (visibility.ClaudeShown)
        {
            usage5h = Math.Max(usage5h, usage.Claude.FiveHour.UsedPercent);
            if (monitor.Claude > worst) worst = monitor.Claude;
            claudeText = "Claude " + Percent(usage.Claude.FiveHour.UsedPercent);
        }
        if (visibility.CodexShown)
        {
            usage5h = Math.Max(usage5h, usage.Codex.FiveHour.UsedPercent);
            if (monitor.Codex > worst) worst = monitor.Codex;
            codexText = "Codex " + Percent(usage.Codex.FiveHour.UsedPercent);
        }

        var next = TrayIconRenderer.Render(usage5h, worst);
        _icon.Icon = next;
        _rendered?.Dispose();
        _rendered = next;

        var parts = new[] { claudeText, codexText }.Where(p => p is not null);
        var joined = string.Join(" · ", parts);
        var status = StatusWord(worst);
        // NotifyIcon.Text is capped (~127 chars) — this line is always short.
        _icon.Text = status is null
            ? (joined.Length == 0 ? "Agent Island" : "Agent Island · " + joined)
            : $"Agent Island · {status}" + (joined.Length == 0 ? "" : " · " + joined);
    }

    private static string Percent(double fraction) => $"{Core.Formatting.PercentInt(fraction)}%";

    private static string? StatusWord(ActivityState state) => state switch
    {
        ActivityState.AuthRequired => Localization.L10n.Tr("Needs attention"),
        ActivityState.RateLimited => Localization.L10n.Tr("Needs attention"),
        ActivityState.Stalled => Localization.L10n.Tr("Needs attention"),
        ActivityState.NeedsYou => Localization.L10n.Tr("Your turn"),
        ActivityState.Working => Localization.L10n.Tr("Running"),
        _ => null,
    };

    /// Windows toast-equivalent for the turn alarm's system notification.
    public void ShowBalloon(string title, string body)
    {
        try
        {
            _icon.ShowBalloonTip(5000, title, body, System.Windows.Forms.ToolTipIcon.None);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        UsageStore.Shared.PropertyChanged -= OnDataChanged;
        ActivityMonitor.Shared.PropertyChanged -= OnDataChanged;
        ProviderVisibilityStore.Shared.PropertyChanged -= OnDataChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _rendered?.Dispose();
    }
}
