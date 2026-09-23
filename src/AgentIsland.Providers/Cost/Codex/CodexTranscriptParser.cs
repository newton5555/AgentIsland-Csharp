using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;

namespace AgentIsland.Providers.Cost.Codex;

public sealed class CodexParseState
{
    public string? SessionId { get; set; }
    public string? ForkedFromId { get; set; }
    public DateTimeOffset? SessionStartedAt { get; set; }
    public string? ProjectId { get; set; }
    public string CurrentModel { get; set; } = CodexTranscriptParser.FallbackModel;
    public bool ModelIsFallback { get; set; } = true;
    public ServiceTier ServiceTier { get; set; }
    public long? LastTotalInput { get; set; }
    public long? LastTotalOutput { get; set; }
}

public sealed record CodexParsedTurn(
    ConsumptionFact Fact,
    string? ForkedFromId,
    DateTimeOffset? SessionStartedAt,
    long CumulativeInput,
    long CumulativeOutput);

/// Parses Codex JSONL into consumption facts. Same-file replay (equal running
/// totals) is dropped here; fork inheritance is applied by CodexReplayPlan.
public static class CodexTranscriptParser
{
    public const string FallbackModel = "gpt-5.4";

    public static (List<CodexParsedTurn> Turns, CodexParseState State, long NextOffset, bool Ok) ParseFromOffset(
        string path,
        CodexParseState? state,
        long startOffset)
    {
        state ??= new CodexParseState();
        var turns = new List<CodexParsedTurn>();
        var nextOffset = startOffset;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return (turns, state, startOffset, false);
            if (startOffset > info.Length) return (turns, state, startOffset, true);

            foreach (var (line, lineEnd) in Jsonl.ReadCompleteLines(path, startOffset))
            {
                nextOffset = lineEnd;
                if (line.Length > 1_000_000) continue;
                using var doc = Jsonl.TryParseLine(line);
                if (doc is null) continue;
                ReadLine(doc.RootElement, path, lineEnd, state, turns);
            }
        }
        catch
        {
            return (turns, state, startOffset, false);
        }

        return (turns, state, nextOffset, true);
    }

    public static List<CodexParsedTurn> ParseFile(string path)
    {
        var (turns, _, _, _) = ParseFromOffset(path, new CodexParseState(), 0);
        return turns;
    }

    private static void ReadLine(
        System.Text.Json.JsonElement root,
        string path,
        long nextOffset,
        CodexParseState state,
        List<CodexParsedTurn> turns)
    {
        var type = Jsonl.GetString(root, "type");
        if (type == "session_meta")
        {
            ReadSessionMeta(root, state);
            return;
        }

        if (type == "turn_context")
        {
            if (Jsonl.GetObject(root, "payload") is { } context)
            {
                if (Jsonl.GetString(context, "model") is { Length: > 0 } model)
                {
                    state.CurrentModel = model;
                    state.ModelIsFallback = false;
                }

                ReadServiceTier(context, state);
            }

            return;
        }

        if (Jsonl.GetObject(root, "payload") is { } payload)
            ReadServiceTier(payload, state);

        if (type != "event_msg") return;
        if (Jsonl.GetObject(root, "payload") is not { } eventPayload) return;
        if (Jsonl.GetString(eventPayload, "type") != "token_count") return;
        if (Jsonl.GetObject(eventPayload, "info") is not { } info) return;

        long? totalIn = null;
        long? totalOut = null;
        if (Jsonl.GetObject(info, "total_token_usage") is { } totals)
        {
            totalIn = Jsonl.GetLong(totals, "input_tokens") ?? 0;
            totalOut = Jsonl.GetLong(totals, "output_tokens") ?? 0;
            if (state.LastTotalInput == totalIn && state.LastTotalOutput == totalOut)
                return;
            state.LastTotalInput = totalIn;
            state.LastTotalOutput = totalOut;
        }

        if (Jsonl.GetObject(info, "last_token_usage") is not { } usage) return;
        var lastInput = Jsonl.GetLong(usage, "input_tokens") ?? 0;
        var cachedInput = Jsonl.GetLong(usage, "cached_input_tokens") ?? 0;
        var outputTokens = Jsonl.GetLong(usage, "output_tokens") ?? 0;
        var reasoning = Jsonl.GetLong(usage, "reasoning_output_tokens") ?? 0;
        if (lastInput == 0 && outputTokens == 0) return;
        if (Jsonl.ParseIso8601(Jsonl.GetString(root, "timestamp")) is not { } timestamp) return;

        var nonCached = Math.Max(0, lastInput - cachedInput);
        var sessionId = state.SessionId ?? "";
        var cumulativeIn = totalIn ?? lastInput;
        var cumulativeOut = totalOut ?? outputTokens;
        var recordIdentity = string.IsNullOrEmpty(sessionId) ? Path.GetFullPath(path) : sessionId;
        var recordId =
            $"{recordIdentity}|{timestamp:o}|{cumulativeIn}|{cumulativeOut}|{lastInput}|{outputTokens}";
        var canonical = Pricing.CanonicalModelName(state.CurrentModel);
        var accounting = reasoning > 0
            ? ReasoningAccounting.IncludedInOutput
            : ReasoningAccounting.Absent;

        var fact = new ConsumptionFact(
            new SourceRef(
                AgentKeys.Codex,
                recordId,
                string.IsNullOrEmpty(sessionId) ? null : sessionId,
                state.ProjectId,
                AccountId: null,
                path,
                nextOffset,
                cumulativeIn,
                cumulativeOut),
            timestamp,
            new ModelRef(state.CurrentModel, canonical, state.ModelIsFallback),
            new TokenBuckets(
                nonCached,
                outputTokens,
                CacheCreation: 0,
                Math.Min(cachedInput, lastInput),
                reasoning,
                accounting),
            new PricingContext(state.ServiceTier, LongContext: false, timestamp, OfficialCostUsd: null));

        turns.Add(new CodexParsedTurn(
            fact,
            state.ForkedFromId,
            state.SessionStartedAt,
            cumulativeIn,
            cumulativeOut));
    }

    private static void ReadSessionMeta(System.Text.Json.JsonElement root, CodexParseState state)
    {
        if (Jsonl.ParseIso8601(Jsonl.GetString(root, "timestamp")) is { } started)
            state.SessionStartedAt ??= started;
        if (Jsonl.GetObject(root, "payload") is not { } payload) return;
        if (Jsonl.GetString(payload, "id") is { Length: > 0 } id)
            state.SessionId ??= id;
        if (Jsonl.GetString(payload, "forked_from_id") is { Length: > 0 } parent)
            state.ForkedFromId ??= parent;
        if (Jsonl.GetString(payload, "cwd") is { Length: > 0 } cwd)
            state.ProjectId ??= cwd;
    }

    private static void ReadServiceTier(System.Text.Json.JsonElement payload, CodexParseState state)
    {
        var raw = Jsonl.GetString(payload, "service_tier")
            ?? Jsonl.GetString(payload, "speed");
        if (raw is null) return;
        state.ServiceTier = raw.ToLowerInvariant() switch
        {
            "fast" => ServiceTier.Fast,
            "priority" => ServiceTier.Priority,
            "standard" or "default" => ServiceTier.Standard,
            _ => state.ServiceTier,
        };
    }
}
