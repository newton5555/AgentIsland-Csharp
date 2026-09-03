using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AgentIsland.Core;
using AgentIsland.UI.Theme;
using AgentIsland.Usage;

namespace AgentIsland.UI;

/// The peek-state glance readout: `32% · 2h` — 5h used percentage tinted in
/// the provider color, reset countdown in quiet white. Rendering rules match
/// the macOS NotchPeekPill: keep the last good value during refresh, `—%` on
/// error, dim window-length fallback when no live countdown exists.
public sealed class NotchPeekPill : TextBlock
{
    private TriggerTool _tool = TriggerTool.Claude;

    public NotchPeekPill()
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas");
        FontSize = 11;
        FontWeight = FontWeights.SemiBold;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public TriggerTool Tool
    {
        get => _tool;
        set => _tool = value;
    }

    public bool Mirrored { get; set; }

    public void Update(WindowUsage usage, bool loading, Model.AlertSeverity severity = Model.AlertSeverity.None)
    {
        Inlines.Clear();
        var tint = severity switch
        {
            Model.AlertSeverity.Critical => IslandColors.AlertRed,
            Model.AlertSeverity.Warning => IslandColors.AlertAmber,
            _ => IslandColors.For(_tool),
        };

        if (usage.HasError && usage.UsedPercent == 0)
        {
            Inlines.Add(Dim("—%", 0.40));
            return;
        }
        if (loading && usage.UsedPercent == 0 && usage.ResetAt is null)
        {
            Inlines.Add(Dim("…", 0.55));
            return;
        }

        // The right-hand pill mirrors: countdown first, percent hugging the
        // logo — exactly like the macOS bar.
        var percentText = $"{Math.Round(Model.QuotaDisplayModeStore.Shared.DisplayValue(usage.UsedPercent))}%";
        var now = DateTimeOffset.Now;
        var countdown = usage.ResetAt is { } resetAt && resetAt > now
            ? CompactCountdown(resetAt - now)
            : null;
        var mirrored = Mirrored;
        // The no-countdown placeholder names the window's REAL period.
        var periodTag = Charts.ChartTile.PeriodLabel(usage, "5h");

        if (mirrored)
        {
            if (countdown is not null) Inlines.Add(Dim(countdown + " · ", 0.70));
            else Inlines.Add(Dim(periodTag + " · ", 0.40));
            Inlines.Add(new Run(percentText) { Foreground = IslandColors.Brush(tint) });
            if (severity != Model.AlertSeverity.None)
            {
                Inlines.Add(new Run(" ⚠") { Foreground = IslandColors.Brush(tint) });
            }
        }
        else
        {
            if (severity != Model.AlertSeverity.None)
            {
                Inlines.Add(new Run("⚠ ") { Foreground = IslandColors.Brush(tint) });
            }
            Inlines.Add(new Run(percentText) { Foreground = IslandColors.Brush(tint) });
            if (countdown is not null) Inlines.Add(Dim(" · " + countdown, 0.70));
            else Inlines.Add(Dim(" · " + periodTag, 0.40));
        }
    }

    public static string CompactCountdown(TimeSpan remaining)
    {
        // Day-unit first: a weekly window reads "6d", never "150h". Then Nh
        // when >= 1h (floored — 107m reads "1h"), Nm under 1h. Never mixed:
        // "1h 47m" is too noisy for a glance pill.
        if (remaining.TotalDays >= 2) return $"{(int)remaining.TotalDays}d";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}h";
        return $"{Math.Max(1, (int)Math.Round(remaining.TotalMinutes))}m";
    }

    private static Run Dim(string text, double opacity) => new(text)
    {
        Foreground = IslandColors.Brush(IslandColors.White(opacity)),
    };
}
