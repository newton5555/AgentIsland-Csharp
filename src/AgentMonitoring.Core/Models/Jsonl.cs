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

    /// <summary>
    /// Streams lines from a file using non-exclusive sharing (ReadWrite | Delete)
    /// so external agent processes actively writing or holding the log file
    /// do not trigger Windows ERROR_SHARING_VIOLATION exceptions.
    /// </summary>
    public static IEnumerable<string> ReadLinesShared(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch
        {
            yield break;
        }

        using (stream)
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
        {
            string? line;
            while (true)
            {
                try
                {
                    line = reader.ReadLine();
                }
                catch
                {
                    break;
                }
                if (line is null) break;
                yield return line;
            }
        }
    }

    /// Reads complete JSONL lines from a byte offset. A torn trailing line
    /// without a newline is omitted so the caller can resume at NextOffset.
    public static IEnumerable<(string Line, long NextOffset)> ReadCompleteLines(string path, long startOffset)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (startOffset < 0) startOffset = 0;
        if (startOffset > stream.Length) yield break;
        stream.Position = startOffset;
        var pending = new List<byte>(256);
        var skippingOversizedLine = false;
        const int maxLineBytes = 1_000_000;
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) yield break;

            var bufferStart = stream.Position - read;
            var consumed = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                if (!skippingOversizedLine)
                {
                    pending.AddRange(buffer.AsSpan(consumed, i - consumed).ToArray());
                    if (pending.Count > 0 && pending[^1] == (byte)'\r')
                        pending.RemoveAt(pending.Count - 1);
                    if (pending.Count > maxLineBytes)
                    {
                        pending.Clear();
                        skippingOversizedLine = true;
                    }
                }

                var nextOffset = bufferStart + i + 1;
                if (!skippingOversizedLine)
                {
                    if (pending.Count > 0)
                        yield return (System.Text.Encoding.UTF8.GetString(pending.ToArray()), nextOffset);
                }

                pending.Clear();
                skippingOversizedLine = false;
                consumed = i + 1;
            }

            if (consumed < read && !skippingOversizedLine)
            {
                pending.AddRange(buffer.AsSpan(consumed, read - consumed).ToArray());
                // A CR byte may still be the terminator on the next read.
                if (pending.Count > maxLineBytes + 1)
                {
                    pending.Clear();
                    skippingOversizedLine = true;
                }
            }
        }
    }
}
