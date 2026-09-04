using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentIsland.Core;
using ZstdSharp;

namespace AgentIsland.Providers.Sessions.DeepSeek;

/// The activity signal reconstructed from one DeepSeek Harness session.
/// Harness mixes route metadata, streaming chunks, tool work, and turn
/// boundaries in the same event stream, so the latest route-bearing event is
/// kept alongside its kind. Hosts can use the route as metadata while still
/// treating activity from every gateway as a local session signal.
public enum DeepSeekActivityKind
{
    None,
    Active,
    Completed,
}

public sealed record DeepSeekActivitySnapshot(
    string? LatestProvider,
    DeepSeekActivityKind LatestKind,
    DateTimeOffset? LatestTimestamp,
    string? Model,
    long? Turn,
    long? Step,
    string? Cwd = null,
    string? Origin = null,
    long? DelegationDepth = null)
{
    public bool IsOfficialRoute => string.Equals(
        LatestProvider, "deepseek-official", StringComparison.OrdinalIgnoreCase);

    public string? TurnKey => Turn is { } turn && Step is { } step
        ? $"{turn}:{step}"
        : null;

    /// Harness marks delegated worker sessions with both an origin and a
    /// positive delegation depth. They are machine fan-out, not a human's
    /// active turn, so the host monitor excludes them just like Codex and
    /// Claude subagent transcripts.
    public bool IsSubagent => string.Equals(
            Origin?.Trim(), "subagent", StringComparison.OrdinalIgnoreCase)
        || DelegationDepth is > 0;
}

/// Parses only the small status projection needed by the activity monitor.
/// Token accounting remains in DeepSeekLogParser; keeping this projection
/// separate lets the monitor evolve without changing cost semantics.
public static class DeepSeekActivityParser
{
    private const int MaxLineLength = 1_000_000;
    private const int BufferSize = 128 * 1024;
    private const long MinUnixMilliseconds = -62_135_596_800_000L;
    private const long MaxUnixMilliseconds = 253_402_300_799_999L;

    /// Parse one compressed DSH stream. Independent zstd frames are consumed
    /// until EOF; a torn final frame leaves the complete prefix available.
    public static DeepSeekActivitySnapshot? ParseFile(string path)
    {
        var accumulator = new Accumulator();
        if (string.IsNullOrWhiteSpace(path)) return null;

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
            // DSH appends while a session is live. A torn tail is expected;
            // the next fingerprinted scan retries after the next write.
        }

        return accumulator.ToSnapshot();
    }

    /// Decompressed JSONL seam used by tests and diagnostics.
    public static DeepSeekActivitySnapshot? ParseLines(IEnumerable<string>? lines)
    {
        var accumulator = new Accumulator();
        if (lines is null) return null;
        try
        {
            foreach (var line in lines) accumulator.ReadLine(line);
        }
        catch
        {
            // Preserve the complete prefix if an enumerable fails mid-pass.
        }
        return accumulator.ToSnapshot();
    }

    private static void Read(TextReader reader, Accumulator accumulator)
    {
        while (reader.ReadLine() is { } line) accumulator.ReadLine(line);
    }

    private sealed class Accumulator
    {
        private string? _currentProvider;
        private string? _currentModel;
        private string? _latestProvider;
        private DeepSeekActivityKind _latestKind;
        private DateTimeOffset? _latestTimestamp;
        private long? _latestTurn;
        private long? _latestStep;
        private string? _cwd;
        private string? _origin;
        private long? _delegationDepth;
        private bool _hasEvent;

        internal void ReadLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLineLength) return;
            using var document = Jsonl.TryParseLine(line);
            if (document is null) return;

            var root = document.RootElement;
            var type = Jsonl.GetString(root, "type");
            if (string.IsNullOrWhiteSpace(type)) return;

            if (type == "session")
            {
                _cwd = Jsonl.GetString(root, "cwd")?.Trim();
                _origin = Jsonl.GetString(root, "origin")?.Trim();
                _delegationDepth = Number(root, "delegationDepth");
                return;
            }

            var data = Jsonl.GetObject(root, "data");
            if (type == "request/header")
            {
                // A new header starts a new request. Clearing a missing
                // provider is safer than inheriting the previous route.
                _currentProvider = HeaderProvider(data);
                _currentModel = HeaderModel(data) ?? _currentModel;
                Record(root, data, _currentProvider, DeepSeekActivityKind.Active);
                return;
            }

            if (type == "request/context")
            {
                var contextProvider = data is { } context
                    ? Jsonl.GetString(context, "provider")
                    : null;
                if (contextProvider is not null) _currentProvider = contextProvider.Trim();
                if (data is { } contextModel
                    && Jsonl.GetString(contextModel, "model") is { Length: > 0 } model)
                {
                    _currentModel = model.Trim();
                }
                Record(root, data, _currentProvider, DeepSeekActivityKind.Active);
                return;
            }

            if (!IsActivityType(type)) return;

            var provider = MessageProvider(data) ?? _currentProvider;
            if (type == "assistant/message" && MessageModel(data) is { Length: > 0 } messageModel)
            {
                _currentModel = messageModel.Trim();
            }

            var kind = type is "assistant/message" or "step/end" or "turn/end"
                ? DeepSeekActivityKind.Completed
                : DeepSeekActivityKind.Active;
            Record(root, data, provider, kind);
        }

        private void Record(
            JsonElement root,
            JsonElement? data,
            string? provider,
            DeepSeekActivityKind kind)
        {
            _hasEvent = true;
            _latestProvider = provider?.Trim();
            _latestKind = kind;
            _latestTimestamp = EventTimestamp(root);
            _latestTurn = data is { } dataObject ? Number(dataObject, "turn") : null;
            _latestStep = data is { } stepObject ? Number(stepObject, "step") : null;
        }

        internal DeepSeekActivitySnapshot? ToSnapshot() => !_hasEvent
            ? null
            : new DeepSeekActivitySnapshot(
                _latestProvider,
                _latestKind,
                _latestTimestamp,
                _currentModel,
                _latestTurn,
                _latestStep,
                _cwd,
                _origin,
                _delegationDepth);
    }

    private static bool IsActivityType(string type) => type is
        "turn/start" or "step/start" or "user/message" or
        "assistant/chunk" or "assistant/message" or
        "reasoning-chunks" or "text-chunks" or "tool-call-chunks" or
        "tool/call" or "tool/result" or "command/run" or "command/done" or
        "step/end" or "turn/end" or "llm/retry" or "llm/retry-started";

    private static string? HeaderProvider(JsonElement? data) =>
        data is { } value
        && Jsonl.GetObject(value, "header") is { } header
        && Jsonl.GetObject(header, "config") is { } config
            ? Jsonl.GetString(config, "provider")
            : null;

    private static string? HeaderModel(JsonElement? data) =>
        data is { } value
        && Jsonl.GetObject(value, "header") is { } header
        && Jsonl.GetObject(header, "config") is { } config
            ? Jsonl.GetString(config, "model")
            : null;

    private static string? MessageProvider(JsonElement? data)
    {
        if (data is not { } value || Jsonl.GetObject(value, "message") is not { } message)
        {
            return null;
        }
        if (Jsonl.GetObject(message, "source") is { } source
            && Jsonl.GetString(source, "provider") is { Length: > 0 } provider)
        {
            return provider;
        }
        return Jsonl.GetString(message, "provider");
    }

    private static string? MessageModel(JsonElement? data)
    {
        if (data is not { } value || Jsonl.GetObject(value, "message") is not { } message)
        {
            return null;
        }
        if (Jsonl.GetObject(message, "source") is { } source
            && Jsonl.GetString(source, "model") is { Length: > 0 } model)
        {
            return model;
        }
        return Jsonl.GetString(message, "model");
    }

    private static long? Number(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer))
        {
            return integer;
        }
        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static DateTimeOffset? EventTimestamp(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("time", out var value)) return null;

        long? epoch = null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            epoch = number;
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var raw = value.GetString();
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                epoch = parsed;
            }
            else if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var date))
            {
                return date;
            }
        }

        if (epoch is not { } unix
            || unix <= 0
            || unix < MinUnixMilliseconds
            || unix > MaxUnixMilliseconds)
        {
            return null;
        }

        // DSH currently writes milliseconds. Accept seconds in fixtures and
        // old streams defensively without changing the public snapshot.
        try
        {
            return unix < 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : DateTimeOffset.FromUnixTimeMilliseconds(unix);
        }
        catch
        {
            return null;
        }
    }
}
