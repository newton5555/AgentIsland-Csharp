using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AgentIsland.Core;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Localization;

namespace AgentIsland.UI;

/// <summary>
/// Modern dark-themed tray popup menu for Agent Island.
/// Provides a sleek, Fluent-styled context menu with subtle 1px border,
/// restrained shadow, clean typography, and native DPI-aware placement.
/// Supports toggling between Normal (Interactive) Mode and Transparent (70% Opacity, Click-through) Mode.
/// </summary>
public sealed class ModernTrayMenu
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly ContextMenu _menu;
    private readonly MenuItem _modeItem;
    private readonly Func<bool>? _isTransparentModeQuery;

    public ModernTrayMenu(
        Action showIsland,
        Action toggleTransparentMode,
        Action openSettings,
        Action exit,
        Action openDailyReport,
        Action openWeeklyReport,
        Action openMonthlyReport,
        Func<bool>? isTransparentModeQuery = null)
    {
        _isTransparentModeQuery = isTransparentModeQuery;

        _menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            Template = CreateContextMenuTemplate(),
            MinWidth = 160,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };

        _modeItem = CreateItem(GetModeItemText(), toggleTransparentMode);
        _menu.Items.Add(_modeItem);
        _menu.Items.Add(CreateSeparator());

        _menu.Items.Add(CreateItem(L10n.Tr("Daily report"), openDailyReport));
        _menu.Items.Add(CreateItem(L10n.Tr("Share weekly report…"), openWeeklyReport));
        _menu.Items.Add(CreateItem(L10n.Tr("Share monthly report…"), openMonthlyReport));
        _menu.Items.Add(CreateSeparator());

        _menu.Items.Add(CreateItem(L10n.Tr("Settings…"), openSettings));
        _menu.Items.Add(CreateSeparator());

        _menu.Items.Add(CreateItem(L10n.Tr("Quit Agent Island"), exit, isDestructive: true));
    }

    private string GetModeItemText()
    {
        bool isTrans = _isTransparentModeQuery?.Invoke() ?? false;
        if (isTrans)
        {
            return L10n.IsChinese
                ? "✓ 恢复正常模式 (当前: 70%透明穿透)"
                : "✓ Exit transparent mode";
        }
        else
        {
            return L10n.IsChinese
                ? "透明穿透模式 (70%透明)"
                : "Transparent mode (70% opacity, click-through)";
        }
    }

    public void Show()
    {
        try
        {
            _modeItem.Header = GetModeItemText();

            var app = Application.Current;
            if (app is not null && app.Dispatcher.CheckAccess())
            {
                if (app.MainWindow is { } win)
                {
                    var hwnd = new WindowInteropHelper(win).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        SetForegroundWindow(hwnd);
                    }
                }
            }
        }
        catch { }

        _menu.IsOpen = true;
    }

    public void ShowNearCursor() => Show();

    public void Hide()
    {
        _menu.IsOpen = false;
    }

    public void Close() => Hide();

    public bool IsOpen => _menu.IsOpen;

    public void Destroy()
    {
        _menu.IsOpen = false;
        _menu.Items.Clear();
    }

    public void UpdateStatus(string summary, ActivityState state)
    {
        // Retained for TrayIcon interface compatibility
    }

    private MenuItem CreateItem(string label, Action action, bool isDestructive = false)
    {
        var item = new MenuItem
        {
            Header = label,
            Template = CreateMenuItemTemplate(isDestructive),
            Cursor = Cursors.Hand,
        };
        item.Click += (_, _) =>
        {
            _menu.IsOpen = false;
            action();
        };
        return item;
    }

    private static ControlTemplate CreateContextMenuTemplate()
    {
        var template = new ControlTemplate(typeof(ContextMenu));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x19, 0x1A, 0x1F)));
        border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF)));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.PaddingProperty, new Thickness(4, 4, 4, 4));

        // Restrained, subtle shadow
        var shadow = new DropShadowEffect
        {
            BlurRadius = 10,
            ShadowDepth = 2,
            Opacity = 0.24,
            Color = Colors.Black,
        };
        border.SetValue(UIElement.EffectProperty, shadow);

        var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        border.AppendChild(presenter);

        template.VisualTree = border;
        return template;
    }

    private static ControlTemplate CreateMenuItemTemplate(bool isDestructive = false)
    {
        var template = new ControlTemplate(typeof(MenuItem));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ItemBorder";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new Thickness(12, 6, 12, 6));
        border.SetValue(Border.MarginProperty, new Thickness(2, 1, 2, 1));

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.Name = "ItemText";
        text.SetBinding(TextBlock.TextProperty, new Binding("Header")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
        });
        text.SetValue(TextBlock.FontFamilyProperty, IslandFonts.Ui);
        text.SetValue(TextBlock.FontSizeProperty, 12.0);
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.Normal);
        text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

        var normalColor = isDestructive ? Color.FromRgb(0xF8, 0x71, 0x71) : Color.FromRgb(0xEE, 0xEE, 0xF2);
        text.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(normalColor));

        border.AppendChild(text);
        template.VisualTree = border;

        var highlightTrigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        var hoverBg = isDestructive
            ? Color.FromArgb(0x24, 0xEF, 0x44, 0x44)
            : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
        highlightTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(hoverBg), "ItemBorder"));
        if (isDestructive)
        {
            highlightTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5)), "ItemText"));
        }
        else
        {
            highlightTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.White, "ItemText"));
        }
        template.Triggers.Add(highlightTrigger);

        return template;
    }

    private static Separator CreateSeparator()
    {
        return new Separator
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(6, 3, 6, 3),
            BorderThickness = new Thickness(0),
        };
    }
}
