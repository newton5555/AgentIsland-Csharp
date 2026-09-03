using System.Text;
using System.Text.Json;

namespace AgentIsland.Core;

public readonly record struct SessionTurnStatus(
    bool IsDone,
    string? Key,
    DateTimeOffset? ActivityDate,
    bool IsRunning = false);

/// Classifies the tail of a transcript: is the latest turn finished (the user
/// is "up"), and which event identifies that turn. Direct port of the macOS
/// SessionTurnState — the turn key + activity date drive alarm dedup and the
/// bookkeeping-grace logic in SessionScanner.
public static class SessionTurnState
{
    public static SessionTurnStatus Claude(IReadOnlyList<string> lines) =>
        ClaudeCore(lines, sidechainIsTheConversation: false);

    /// For agent transcripts, where every line is a sidechain by definition.
    public static SessionTurnStatus ClaudeAgent(IReadOnlyList<string> lines) =>
        ClaudeCore(lines, sidechainIsTheConversation: true);

    private static SessionTurnStatus ClaudeCore(IReadOnlyList<string> lines, bool sidechainIsTheConversation)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            using var doc = Jsonl.TryParseLine(lines[i]);
            if (doc is null) continue;
            var root = doc.RootElement;
            // Older Claude Code interleaves subagent traffic into the main
            // transcript marked isSidechain — a subagent's end_turn there is
            // not the user's turn and must not classify the main session.
            if (!sidechainIsTheConversation && Jsonl.GetBool(root, "isSidechain") == true) continue;
            var type = Jsonl.GetString(root, "type");
            switch (type)
            {
                case "assistant":
                {
                    string? stop = null;
                    if (Jsonl.GetObject(root, "message") is { } message)
                        stop = Jsonl.GetString(message, "stop_reason");
                    // Claude Code writes rate-limit / API-error lines with the
                    // SAME envelope as a finished turn (type:assistant,
                    // stop_reason:"stop_sequence") but flags them
                    // isApiErrorMessage:true (e.g. "You've hit your session
                    // limit · resets 2:20am"). Treating those as a completed
                    // turn fired a false "it's your turn" alarm on every
                    // rate-limit — mirror the macOS fix and never mark them done.
                    var isApiError = Jsonl.GetBool(root, "isApiErrorMessage") == true;
                    var isDone = !isApiError && stop is "end_turn" or "stop_sequence" or "stop";
                    return new SessionTurnStatus(isDone, Key(root, lines[i]), Date(root));
                }
                case "user":
                    // Agent runs usually end on the final tool result (marked
                    // toolEndsTurn) rather than an assistant stop; without
                    // this a finished agent never reads as done.
                    if (sidechainIsTheConversation && Jsonl.GetBool(root, "toolEndsTurn") == true)
                        return new SessionTurnStatus(true, Key(root, lines[i]), Date(root));
                    return new SessionTurnStatus(false, Key(root, lines[i]), Date(root));
                default:
                    continue;
            }
        }
        return new SessionTurnStatus(false, null, null);
    }

    public static SessionTurnStatus Codex(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            using var doc = Jsonl.TryParseLine(lines[i]);
            if (doc is null) continue;
            var root = doc.RootElement;
            var type = Jsonl.GetString(root, "type");
            var payload = Jsonl.GetObject(root, "payload");
            var payloadType = payload is { } p ? Jsonl.GetString(p, "type") : null;
            if (type == "event_msg")
            {
                if (IsCodexUserOrStart(payloadType))
                    return new SessionTurnStatus(false, Key(root, lines[i]), Date(root));
                if (payloadType is "task_complete" or "turn/completed")
                    return new SessionTurnStatus(true, Key(root, lines[i]), Date(root));
            }
            if (type == "response_item"
                && payloadType == "message"
                && payload is { } pm
                && Jsonl.GetString(pm, "role") == "user")
            {
                return new SessionTurnStatus(false, Key(root, lines[i]), Date(root));
            }
        }
        return new SessionTurnStatus(false, null, null);
    }

    /// Grok appends one JSON object per session event to `updates.jsonl` with
    /// an explicit `params.update.sessionUpdate` discriminator — and unlike
    /// Claude it names the turn boundary outright: `turn_completed`. Anything
    /// written after it (tool calls, streaming chunks, retry_state) means the
    /// turn is open again. Timestamps here are unix SECONDS, not the
    /// milliseconds Claude Desktop uses.
    public static SessionTurnStatus Grok(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            using var doc = Jsonl.TryParseLine(lines[i]);
            if (doc is null) continue;
            if (Jsonl.GetObject(doc.RootElement, "params") is not { } parameters) continue;
            if (Jsonl.GetObject(parameters, "update") is not { } update) continue;
            if (Jsonl.GetString(update, "sessionUpdate") is not { } kind) continue;
            var stamp = UnixSeconds(Jsonl.GetDouble(doc.RootElement, "timestamp"));
            var seconds = stamp is { } s
                ? s.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "";
            return new SessionTurnStatus(kind == "turn_completed", seconds + ":" + kind, stamp);
        }
        return new SessionTurnStatus(false, null, null);
    }

    /// For sessions whose transcript has no verified turn boundary (Gemini's
    /// $set checkpoint stream): never claims "done", so the engine derives
    /// working/idle purely from file recency and can never raise a false
    /// "your turn" alarm on a format we have not verified. The restraint is
    /// deliberate — do not turn this into a heuristic without real samples.
    /// Cursor: one bubble per message, `type` 1 = user, 2 = assistant
    /// (verified against live conversation text, 2026-08-08). The assistant
    /// having spoken last is the same turn boundary Claude's stop_reason
    /// gives us; the caller adds the quiet gap that separates "still
    /// streaming" from "finished".
    public static SessionTurnStatus Cursor(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return new SessionTurnStatus(false, null, null);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(lines[^1]);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return new SessionTurnStatus(false, null, null);
            }
            var type = root.TryGetProperty("type", out var t) && t.TryGetInt32(out var parsed) ? parsed : 0;
            string? key = root.TryGetProperty("bubbleId", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.String
                ? "cursor:" + b.GetString()
                : null;
            // Cursor writes bubble timestamps as an ISO-8601 string with
            // milliseconds ("2026-08-08T19:46:26.661Z") — verified against
            // the real store. Numbers are handled defensively.
            DateTimeOffset? stamp = null;
            if (root.TryGetProperty("createdAt", out var raw))
            {
                if (raw.ValueKind == System.Text.Json.JsonValueKind.String
                    && DateTimeOffset.TryParse(raw.GetString(), out var iso))
                {
                    stamp = iso;
                }
                else if (raw.ValueKind == System.Text.Json.JsonValueKind.Number && raw.TryGetInt64(out var epoch))
                {
                    stamp = epoch > 100_000_000_000L
                        ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                        : DateTimeOffset.FromUnixTimeSeconds(epoch);
                }
            }
            return new SessionTurnStatus(type == 2, key, stamp);
        }
        catch (Exception)
        {
            return new SessionTurnStatus(false, null, null);
        }
    }

    public static SessionTurnStatus MtimeOnly(IReadOnlyList<string> lines) =>
        new(false, null, null);

    /// Antigravity transcript records are `{step_index, source, type,
    /// status, created_at, content, tool_calls…}` where source is
    /// USER_EXPLICIT / SYSTEM / MODEL. Tool steps are ALSO source:MODEL
    /// (verified on real transcripts, 2026-08-08), so "MODEL last" alone
    /// false-fires mid-run — the agent has spoken only when the last MODEL
    /// step is a PLANNER_RESPONSE with real content and no tool_calls.
    /// When background tasks or tool calls are still RUNNING, a subsequent
    /// wait/confirmation PLANNER_RESPONSE must not prematurely mark the turn
    /// done; the turn stays Working until background work completes.
    public static SessionTurnStatus Antigravity(IReadOnlyList<string> lines)
    {
        (string Source, string? Key, DateTimeOffset? Stamp, bool Spoke)? candidate = null;
        var seenSteps = new HashSet<int>();
        // The transcript records a background command as RUNNING, then emits
        // a later SYSTEM message when its task finishes. Scan newest-first so
        // a completion is known before we reach the older RUNNING record.
        var completedTaskIds = new HashSet<string>(StringComparer.Ordinal);
        var hasRunningTask = false;

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            using var doc = Jsonl.TryParseLine(lines[i]);
            if (doc is null) continue;
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) continue;

            int? stepIndex = root.TryGetProperty("step_index", out var stepProp)
                && stepProp.TryGetInt32(out var idx)
                ? idx
                : null;
            var status = Jsonl.GetString(root, "status");
            var source = Jsonl.GetString(root, "source");
            var type = Jsonl.GetString(root, "type");
            var content = Jsonl.GetString(root, "content");
            var isRunningStep = string.Equals(status, "RUNNING", StringComparison.OrdinalIgnoreCase);
            var taskId = isRunningStep || IsAntigravityTaskFinished(source, type, status, content)
                ? AntigravityTaskId(content)
                : null;

            if (taskId is not null && IsAntigravityTaskFinished(source, type, status, content))
            {
                completedTaskIds.Add(taskId);
            }

            if (stepIndex is { } stepIdx)
            {
                if (seenSteps.Add(stepIdx)
                    && isRunningStep
                    && (taskId is null || !completedTaskIds.Contains(taskId)))
                {
                    hasRunningTask = true;
                }
            }
            else if (isRunningStep
                && (taskId is null || !completedTaskIds.Contains(taskId)))
            {
                hasRunningTask = true;
            }

            if (source is null) continue;

            DateTimeOffset? stamp = null;
            if (root.TryGetProperty("created_at", out var created))
            {
                if (created.ValueKind == System.Text.Json.JsonValueKind.String
                    && DateTimeOffset.TryParse(created.GetString(), out var iso))
                {
                    stamp = iso;
                }
                else if (created.ValueKind == System.Text.Json.JsonValueKind.Number
                    && created.TryGetDouble(out var seconds))
                {
                    stamp = seconds > 100_000_000_000d
                        ? DateTimeOffset.FromUnixTimeMilliseconds((long)seconds)
                        : DateTimeOffset.FromUnixTimeSeconds((long)seconds);
                }
            }
            string? key = stepIndex is { } index ? "ag:" + index : null;

            if (candidate is null && source is "MODEL" or "USER_EXPLICIT")
            {
                var spoke = source == "MODEL"
                    && Jsonl.GetString(root, "type") == "PLANNER_RESPONSE"
                    && !AntigravityHasToolCalls(root)
                    && Jsonl.GetString(root, "content") is { Length: > 0 };
                candidate = (source, key, stamp, spoke);
            }

            if (source == "USER_EXPLICIT")
            {
                break;
            }
        }

        if (candidate is not { } c) return new SessionTurnStatus(false, null, null);

        if (c.Source == "USER_EXPLICIT")
        {
            return new SessionTurnStatus(false, c.Key, c.Stamp);
        }

        var isDone = c.Spoke && !hasRunningTask;
        return new SessionTurnStatus(isDone, c.Key, c.Stamp, IsRunning: hasRunningTask);
    }

    private static bool IsAntigravityTaskFinished(
        string? source, string? type, string? status, string? content)
    {
        if (type == "RUN_COMMAND"
            && string.Equals(status, "DONE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return source == "SYSTEM"
            && content?.Contains("finished with result", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? AntigravityTaskId(string? content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        const string marker = "task id";
        var markerStart = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerStart < 0) return null;

        var start = markerStart + marker.Length;
        while (start < content.Length && char.IsWhiteSpace(content[start])) start++;
        if (start < content.Length && content[start] == ':')
        {
            start++;
            while (start < content.Length && char.IsWhiteSpace(content[start])) start++;
        }
        if (start >= content.Length) return null;

        var quote = content[start] is '"' or '\'' ? content[start] : '\0';
        if (quote != '\0')
        {
            start++;
            var endQuote = content.IndexOf(quote, start);
            return endQuote > start ? content[start..endQuote] : null;
        }

        var end = start;
        while (end < content.Length
            && !char.IsWhiteSpace(content[end])
            && content[end] is not '"' and not '\'')
        {
            end++;
        }
        return end > start ? content[start..end] : null;
    }

    private static bool AntigravityHasToolCalls(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("tool_calls", out var calls)) return false;
        return calls.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Null => false,
            System.Text.Json.JsonValueKind.Array => calls.GetArrayLength() > 0,
            _ => true,
        };
    }

    private static bool IsCodexUserOrStart(string? type)
    {
        if (type is null) return false;
        return type == "task_started"
            || type == "turn/started"
            || type == "user_message"
            || type.EndsWith("/task_started", StringComparison.Ordinal)
            || type.EndsWith("/user_message", StringComparison.Ordinal);
    }

    private static string Key(JsonElement root, string line)
    {
        if (Jsonl.GetString(root, "uuid") is { Length: > 0 } uuid) return uuid;
        if (Jsonl.GetString(root, "id") is { Length: > 0 } id) return id;
        if (Jsonl.GetObject(root, "payload") is { } payload)
        {
            foreach (var field in new[] { "turn_id", "id", "item_id", "call_id" })
            {
                if (Jsonl.GetString(payload, field) is { Length: > 0 } value) return value;
            }
        }
        var bytes = Encoding.UTF8.GetBytes(line);
        var encoded = Convert.ToBase64String(bytes);
        return encoded.Length <= 160 ? encoded : encoded[..160];
    }

    private static DateTimeOffset? Date(JsonElement root)
    {
        if (Jsonl.GetString(root, "timestamp") is { } timestamp
            && Jsonl.ParseIso8601(timestamp) is { } parsed)
        {
            return parsed;
        }
        if (Jsonl.GetObject(root, "payload") is { } payload)
        {
            foreach (var field in new[] { "completed_at", "started_at" })
            {
                if (UnixSeconds(Jsonl.GetDouble(payload, field)) is { } parsedSeconds)
                    return parsedSeconds;
            }
        }
        return null;
    }

    /// FromUnixTimeMilliseconds throws on out-of-range input; a corrupt or
    /// foreign-unit timestamp must not fault the scan, so anything outside
    /// the representable range reads as "no date" instead.
    private static DateTimeOffset? UnixSeconds(double? seconds)
    {
        if (seconds is not { } value) return null;
        var ms = value * 1000;
        if (ms is >= -62_135_596_800_000 and <= 253_402_300_799_999)
            return DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
        return null;
    }
}
