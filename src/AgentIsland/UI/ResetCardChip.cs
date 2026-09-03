using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;
using AgentIsland.Usage;

namespace AgentIsland.UI;

/// Banked Codex resets, drawn as a tiny UPRIGHT card — portrait like a card
/// you hold, faced in the Codex logo blue, with the OpenAI mark printed on
/// it and "×N" beside it. No container box around the pair. Clicking it
/// opens the detail popup: each card + how long it stays valid. Always
/// visible, ×0 included (the face dims when empty) — the escape hatches of
/// the weekly-only quota era deserve a permanent slot, not a surprise.
public sealed class ResetCardChip : StackPanel
{
    private readonly Border _face;
    private readonly System.Windows.Shapes.Path _mark;
    private readonly TextBlock _count;
    private readonly Popup _popup;
    private readonly StackPanel _popupBody;

    private int _cards;
    private IReadOnlyList<ResetCard> _details = Array.Empty<ResetCard>();

    public ResetCardChip()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent; // hit-test the whole pair

        _mark = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("F1 " + BrandGeometry.OpenAiPath),
            Width = 9,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Portrait card face in the Codex blue: gradient, a top sheen, a
        // catchlight border, and a drop shadow — reads as an object, not a
        // badge. The sheen rides in an inner border so one Border's corner
        // radius clips it.
        var sheen = new Border
        {
            CornerRadius = new CornerRadius(2.4),
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(IslandColors.White(0.30), 0),
                    new GradientStop(Colors.Transparent, 0.45),
                },
                new Point(0, 0), new Point(0, 1)),
            Child = _mark,
        };
        _face = new Border
        {
            Width = 13.5,
            Height = 19,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(0.6),
            Child = sheen,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                ShadowDepth = 1.1,
                Direction = 270,
                BlurRadius = 4,
                Color = Colors.Black,
                Opacity = 0.5,
            },
        };
        Children.Add(_face);

        _count = new TextBlock
        {
            FontFamily = IslandFonts.Mono,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 0, 0),
        };
        Children.Add(_count);

        _popupBody = new StackPanel { Margin = new Thickness(14) };
        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            VerticalOffset = 6,
            Child = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = IslandColors.Brush(IslandColors.AlarmBackground),
                BorderBrush = IslandColors.Brush(IslandColors.White(0.10)),
                BorderThickness = new Thickness(1),
                MinWidth = 210,
                Child = _popupBody,
                Effect = new DropShadowEffect
                {
                    ShadowDepth = 3,
                    Direction = 270,
                    BlurRadius = 14,
                    Color = Colors.Black,
                    Opacity = 0.5,
                },
            },
        };
        MouseLeftButtonUp += (_, e) =>
        {
            BuildPopupBody();
            _popup.IsOpen = !_popup.IsOpen;
            e.Handled = true;
        };
        _popup.Closed += (_, _) => PopupClosed?.Invoke(this, EventArgs.Empty);

        Apply();
    }

    /// The island's hover-out collapse must not fire while the details popup
    /// is up: the popup grabs the mouse the moment it opens, the silhouette
    /// sees a MouseLeave, and 80ms later the panel folds — taking the popup
    /// with it ("it just snaps shut on its own").
    public bool IsPopupOpen => _popup.IsOpen;

    /// Raised when the popup closes, so the island can re-evaluate the
    /// deferred collapse it suppressed while the popup was up.
    public event EventHandler? PopupClosed;

    public void Update(int? cards, IReadOnlyList<ResetCard>? details)
    {
        var count = cards ?? 0;
        var list = details ?? Array.Empty<ResetCard>();
        if (count == _cards && list.Count == _details.Count) return;
        _cards = count;
        _details = list;
        Apply();
    }

    private void Apply()
    {
        var live = _cards > 0;
        var codex = IslandColors.Codex;
        _face.Background = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(WithAlpha(codex, live ? 0.95 : 0.30), 0),
                new GradientStop(WithAlpha(codex, live ? 0.45 : 0.14), 1),
            },
            new Point(0, 0), new Point(1, 1));
        _face.BorderBrush = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(IslandColors.White(0.45), 0),
                new GradientStop(WithAlpha(codex, 0.15), 1),
            },
            new Point(0, 0), new Point(0, 1));
        // OpenAI mark printed dark on the face, like ink on plastic.
        _mark.Fill = IslandColors.Brush(
            Color.FromRgb(0x08, 0x1A, 0x33), live ? 0.9 : 0.5);
        _count.Text = $"×{_cards}";
        _count.Foreground = IslandColors.Brush(IslandColors.White(live ? 0.78 : 0.42));
        ToolTip = Localization.L10n.TrFormat("{0} banked resets available", _cards);
    }

    private void BuildPopupBody()
    {
        _popupBody.Children.Clear();
        _popupBody.Children.Add(new TextBlock
        {
            Text = Localization.L10n.TrFormat("{0} banked resets available", _cards),
            FontFamily = IslandFonts.Ui,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = IslandColors.Brush(IslandColors.White(0.85)),
        });
        if (_details.Count == 0)
        {
            _popupBody.Children.Add(new TextBlock
            {
                Text = _cards > 0
                    ? Localization.L10n.Tr("Details unavailable right now.")
                    : Localization.L10n.Tr("Earned resets appear here with their expiry."),
                FontFamily = IslandFonts.Ui,
                FontSize = 11,
                Foreground = IslandColors.Brush(IslandColors.White(0.45)),
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 220,
            });
            return;
        }
        foreach (var card in _details)
        {
            var row = new DockPanel { Margin = new Thickness(0, 9, 0, 0) };
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = IslandColors.Brush(IslandColors.Codex),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            var expiry = new TextBlock
            {
                Text = ExpiryText(card),
                FontFamily = IslandFonts.Mono,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = IslandColors.Brush(IslandColors.White(0.5)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0),
            };
            DockPanel.SetDock(expiry, Dock.Right);
            row.Children.Add(expiry);
            row.Children.Add(new TextBlock
            {
                Text = card.Title,
                FontFamily = IslandFonts.Ui,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = IslandColors.Brush(IslandColors.White(0.8)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            _popupBody.Children.Add(row);
        }
    }

    private static string ExpiryText(ResetCard card)
    {
        if (card.ExpiresAt is not { } expires) return "—";
        var remaining = expires - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return Localization.L10n.Tr("expired");
        return Localization.L10n.TrFormat(
            "valid {0} · {1}", CompactRemaining(remaining), expires.ToLocalTime().ToString("M/d"));
    }

    private static string CompactRemaining(TimeSpan remaining)
    {
        if (remaining.TotalDays >= 2) return $"{(int)remaining.TotalDays}d";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}h";
        return $"{Math.Max(1, (int)Math.Round(remaining.TotalMinutes))}m";
    }

    private static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)(alpha * 255), color.R, color.G, color.B);
}
