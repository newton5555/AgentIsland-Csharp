using System.Windows;
using System.Windows.Controls;

namespace AgentIsland.UI;

/// WPF's default ComboBox is a light-theme control — white face, grey
/// chrome — and it read as exactly that on the dark settings page (owner
/// review, 2026-08-09: 质感太差). One dark template, compiled in
/// SettingsStyles.xaml, shared by every picker.
public static class DarkComboStyle
{
    private static Style? _style;

    public static Style Style
    {
        get
        {
            if (_style == null)
            {
                var dict = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/AgentIsland;component/UI/SettingsStyles.xaml", UriKind.Absolute)
                };
                _style = (Style)dict["DarkComboBoxStyle"];
            }
            return _style;
        }
    }

    public static ComboBox Apply(ComboBox box)
    {
        box.Style = Style;
        return box;
    }
}
