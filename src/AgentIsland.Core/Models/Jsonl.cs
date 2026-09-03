using System.Text.Json;

namespace AgentIsland.Core;

/// Tolerant helpers for reading JSONL transcript events. Transcript lines are
/// written by external tools mid-flight, so every accessor treats missing or
/// mistyped fields as absent rather than throwing.
public static class Jsonl
{
    public static JsonDocument? TryParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try { return JsonDocument.Parse(line); }
        catch { return null; }
    }

    public static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    public static double? GetDouble(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    }

    public static long? GetLong(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number) return null;
        return value.TryGetInt64(out var l) ? l : (long)value.GetDouble();
    }

    public static bool? GetBool(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public static JsonElement? GetObject(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.Object ? value : null;
    }

    /// ISO8601 with or without fractional seconds — matches the two formatters
    /// the macOS app keeps.
    public static DateTimeOffset? ParseIso8601(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        return DateTimeOffset.TryParse(
            raw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }
}
