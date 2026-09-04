using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Core.Cost;
using ZstdSharp;

namespace AgentIsland.Providers.Cost.DeepSeek;

/// Reads DeepSeek Harness' local event stream across every configured route.
/// DSH stores one or more zstd frames containing JSONL records; a usage sample
/// can arrive first as an `assistant/chunk` event and then again as the final
/// `assistant/message`. Those two records describe one (route, turn, step), so
/// the later sample replaces the earlier one instead of being added twice.
/// Route filtering belongs to the account-balance and activity surfaces;
/// token usage is the complete local Harness ledger, including `my-gateway`.
public static class DeepSeekLogParser
{
    private const string FallbackModel = "deepseek";
    private const int MaxLineLength = 1_000_000;
    private const int BufferSize = 128 * 1024;
    private const long MinUnixMilliseconds = -62_135_596_800_000L;
    private const long MaxUnixMilliseconds = 253_402_300_799_999L;

    /// Parse one DSH session file. `.jsonl.zstd` is the on-disk format, while
    /// accepting plain `.jsonl` keeps the parser useful for diagnostics and
    /// small fixtures without weakening the production reader's file filter.
    /// If a session is being appended and its last zstd frame is torn, all
    /// complete records read before the decoder error are still returned.
    public static List<TokenEvent> ParseFile(string path)
    {
        var accumulator = new Accumulator();
        if (string.IsNullOrWhiteSpace(path)) return accumulator.ToEvents();

        try
        {
            using var file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.SequentialScan);

            if (path.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase))
            {
                // DSH writes independent frames. DecompressionStream advances
                // across all concatenated frames; ReadLine may throw on a torn
                // tail, and the accumulator deliberately survives that throw.
                using var zstd = new DecompressionStream(
                    file, BufferSize, checkEndOfStream: true, leaveOpen: false);
                using var reader = new StreamReader(
                    zstd, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                    BufferSize, leaveOpen: false);
                Read(reader, accumulator);
            }
            else
            {
                using var reader = new StreamReader(
                    file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                    BufferSize, leaveOpen: false);
                Read(reader, accumulator);
            }
        }
        catch
        {
            // Files are appended while a session is running. A disappearing
            // file, malformed line, or torn final zstd frame is a partial
            // scan; the next poll sees the changed fingerprint and retries.
        }

        return accumulator.ToEvents();
    }

    /// Parser seam for unit tests and callers that already have decompressed
    /// JSONL. Malformed lines are ignored exactly as they are in ParseFile.
    public static List<TokenEvent> ParseLines(IEnumerable<string>? lines)
    {
        var accumulator = new Accumulator();
        if (lines is null) return accumulator.ToEvents();
        try
        {
            foreach (var line in lines) accumulator.ReadLine(line);
        }
        catch
        {
            // An enumerable backed by a live stream can fail mid-pass. Keep
            // the records that were already decoded.
        }
        return accumulator.ToEvents();
    }

    private static void Read(TextReader reader, Accumulator accumulator)
    {
        while (true)
        {
            // StreamReader.ReadLine returns a final unterminated line. If a
            // writer was torn in the middle of a JSON object, TryParseLine
            // rejects it; complete final records remain valid and count.
            var line = reader.ReadLine();
            if (line is null) break;
            accumulator.ReadLine(line);
        }
    }

    private sealed class Accumulator
    {
        private readonly List<Sample> _samples = new();
        private readonly Dictionary<UsageKey, int> _replacementIndexes = new();
        private string _currentModel = FallbackModel;
        private string? _currentProvider;

        internal void ReadLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLineLength) return;
            using var document = Jsonl.TryParseLine(line);
            if (document is null) return;

            var root = document.RootElement;
            var type = Jsonl.GetString(root, "type");
            if (type == "request/header")
            {
                UpdateHeader(root);
                return;
            }

            var data = Jsonl.GetObject(root, "data");
            if (data is not { } dataObject) return;

            JsonElement usage;
            string? model = null;
            if (type == "assistant/chunk")
            {
                if (Jsonl.GetObject(dataObject, "chunk") is not { } chunk
                    || Jsonl.GetString(chunk, "type") != "usage"
                    || Jsonl.GetObject(chunk, "usage") is not { } chunkUsage)
                {
                    return;
                }
                usage = chunkUsage;
                model = _currentModel;
                // A chunk inherits the route selected by the request header.
                // It is part of the replacement key so two routes that reuse
                // a turn/step pair cannot erase each other's token sample.
            }
            else if (type == "assistant/message")
            {
                if (Jsonl.GetObject(dataObject, "usage") is not { } messageUsage)
                {
                    return;
                }
                usage = messageUsage;
                model = MessageModel(dataObject) ?? _currentModel;
                _currentProvider = MessageProvider(dataObject) ?? _currentProvider;
            }
            else
            {
                return;
            }

            var input = Count(usage, "inputTokens");
            var output = Count(usage, "outputTokens");
            var cacheRead = Count(usage, "cacheReadTokens");
            // DSH calls this cacheWriteTokens. Accept the longer spelling as
            // well because it is used by a few early local fixtures.
            var cacheWrite = Count(usage, "cacheWriteTokens");
            if (cacheWrite == 0) cacheWrite = Count(usage, "cacheCreationTokens");
            if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0)
            {
                // reasoningTokens is intentionally not added: DSH's own
                // usageTokens projection is input + cache read/write + output.
                return;
            }

            if (EventTimestamp(root) is not { } timestamp) return;
            model = string.IsNullOrWhiteSpace(model) ? FallbackModel : model.Trim();
            if (model.Length == 0) model = FallbackModel;
            if (type == "assistant/message") _currentModel = model;

            var tokenEvent = new TokenEvent(
                TriggerTool.DeepSeek,
                timestamp,
                model,
                input,
                output,
                cacheWrite,
                cacheRead);

            var key = ReplacementKey(dataObject, _currentProvider);
            if (key is { } replacement && _replacementIndexes.TryGetValue(replacement, out var index))
            {
                _samples[index] = new Sample(tokenEvent);
            }
            else
            {
                if (key is { } newKey) _replacementIndexes[newKey] = _samples.Count;
                _samples.Add(new Sample(tokenEvent));
            }
        }

        private void UpdateHeader(JsonElement root)
        {
            if (Jsonl.GetObject(root, "data") is not { } data) return;
            if (Jsonl.GetObject(data, "header") is not { } header) return;
            if (Jsonl.GetObject(header, "config") is not { } config) return;
            _currentProvider = Jsonl.GetString(config, "provider")?.Trim();
            if (Jsonl.GetString(config, "model") is { Length: > 0 } model)
            {
                _currentModel = model.Trim();
            }
        }

        private static string? MessageModel(JsonElement data)
        {
            if (Jsonl.GetObject(data, "message") is not { } message) return null;
            if (Jsonl.GetObject(message, "source") is { } source
                && Jsonl.GetString(source, "model") is { Length: > 0 } sourceModel)
            {
                return sourceModel;
            }
            // A short-lived event shape put model directly on message.
            return Jsonl.GetString(message, "model");
        }

        private static string? MessageProvider(JsonElement data)
        {
            if (Jsonl.GetObject(data, "message") is not { } message) return null;
            if (Jsonl.GetObject(message, "source") is { } source
                && Jsonl.GetString(source, "provider") is { Length: > 0 } sourceProvider)
            {
                return sourceProvider.Trim();
            }
            return Jsonl.GetString(message, "provider")?.Trim();
        }

        private static UsageKey? ReplacementKey(JsonElement data, string? provider)
        {
            var turn = Number(data, "turn");
            var step = Number(data, "step");
            return turn is >= 0 && step is >= 0
                ? new UsageKey(provider, turn.Value, step.Value)
                : null;
        }

        private static DateTimeOffset? EventTimestamp(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("time", out var value))
            {
                return null;
            }

            long? milliseconds = null;
            if (value.ValueKind == JsonValueKind.Number)
            {
                milliseconds = Number(value);
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                var raw = value.GetString();
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    milliseconds = parsed;
                }
                else if (DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedDate))
                {
                    return parsedDate;
                }
            }

            if (milliseconds is not { } unix
                || unix <= 0
                || unix < MinUnixMilliseconds
                || unix > MaxUnixMilliseconds)
            {
                return null;
            }
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(unix);
            }
            catch
            {
                return null;
            }
        }

        private static long Count(JsonElement parent, string property)
        {
            var value = Number(parent, property);
            return value is >= 0 ? value.Value : 0;
        }

        private static long? Number(JsonElement parent, string property)
        {
            if (parent.ValueKind != JsonValueKind.Object
                || !parent.TryGetProperty(property, out var value))
            {
                return null;
            }
            return Number(value);
        }

        private static long? Number(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Number) return null;
            if (value.TryGetInt64(out var integer)) return integer;
            if (!value.TryGetDouble(out var floating)
                || !double.IsFinite(floating)
                || floating < long.MinValue
                || floating > long.MaxValue)
            {
                return null;
            }
            return (long)floating;
        }

        internal List<TokenEvent> ToEvents() => _samples.Select(sample => sample.Event).ToList();

        private readonly record struct UsageKey(string? Provider, long Turn, long Step);
        private readonly record struct Sample(TokenEvent Event);
    }
}
