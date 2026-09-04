using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace AgentIsland.Avalonia.Converters;

/// <summary>
/// Resolves a color key (e.g. "IslandCodexBrush", "IslandDeepSeekBrush", or hex color "#7B61FF")
/// into an Avalonia IBrush.
/// </summary>
public sealed class ColorKeyToBrushConverter : IValueConverter
{
    public static readonly ColorKeyToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return ResolveBrush(value as string);
    }

    public static IBrush ResolveBrush(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (Application.Current != null &&
                Application.Current.TryGetResource(key, null, out var resource) &&
                resource is IBrush brush)
            {
                return brush;
            }

            // Also try appending "Brush" if missing (e.g. "IslandCodex" -> "IslandCodexBrush")
            if (!key.EndsWith("Brush", StringComparison.Ordinal) &&
                Application.Current != null &&
                Application.Current.TryGetResource(key + "Brush", null, out var suffixResource) &&
                suffixResource is IBrush suffixBrush)
            {
                return suffixBrush;
            }

            // Support direct hex/color string (e.g. "#7B61FF", "Purple")
            if (Color.TryParse(key, out var parsedColor))
            {
                return new SolidColorBrush(parsedColor);
            }
        }

        // Fallback to IslandCodexBrush or a safe purple
        if (Application.Current != null &&
            Application.Current.TryGetResource("IslandCodexBrush", null, out var fallback) &&
            fallback is IBrush fallbackBrush)
        {
            return fallbackBrush;
        }

        return Brushes.MediumPurple;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
