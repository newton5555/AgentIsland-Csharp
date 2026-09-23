namespace AgentIsland.Providers.Cost.Codex;

public sealed record CodexUsageCheckpoint(DateTimeOffset Timestamp, long Input, long Output);

public sealed record CodexReplayResult(
    IReadOnlyList<CodexParsedTurn> KeptTurns,
    IReadOnlySet<string> PassedBaselineChildren);

/// Drops inherited parent-history prefixes from forked Codex sessions.
public static class CodexReplayPlan
{
    public static CodexReplayResult Apply(
        IReadOnlyList<CodexParsedTurn> turns,
        IReadOnlyDictionary<string, IReadOnlyList<CodexUsageCheckpoint>>? knownParentHistory = null,
        IReadOnlySet<string>? alreadyPassedBaselineChildren = null)
    {
        var passedBaseline = alreadyPassedBaselineChildren is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(alreadyPassedBaselineChildren, StringComparer.Ordinal);
        if (turns.Count == 0)
            return new CodexReplayResult(Array.Empty<CodexParsedTurn>(), passedBaseline);

        var parentHistory = new Dictionary<string, List<CodexUsageCheckpoint>>(StringComparer.Ordinal);
        if (knownParentHistory is not null)
        {
            foreach (var (session, checkpoints) in knownParentHistory)
            {
                if (string.IsNullOrEmpty(session)) continue;
                parentHistory[session] = checkpoints.ToList();
            }
        }

        // A forked session can itself be the parent of a later fork. Its raw
        // cumulative checkpoints remain meaningful even when its inherited
        // prefix is filtered from the billed facts.
        foreach (var turn in turns)
        {
            var session = turn.Fact.Source.SessionId;
            if (session is null) continue;
            AddCheckpoint(parentHistory, session,
                new CodexUsageCheckpoint(turn.Fact.Timestamp, turn.CumulativeInput, turn.CumulativeOutput));
        }
        var parentHistoryView = parentHistory.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<CodexUsageCheckpoint>)pair.Value,
            StringComparer.Ordinal);

        var childBaselines = new Dictionary<string, (long Input, long Output)>(StringComparer.Ordinal);
        foreach (var group in turns
            .Where(turn => turn.ForkedFromId is { Length: > 0 })
            .GroupBy(ChildIdentity, StringComparer.Ordinal))
        {
            var first = group.OrderBy(turn => turn.Fact.Timestamp).First();
            var baseline = BaselineFor(
                first.ForkedFromId,
                first.SessionStartedAt,
                first.FirstTurnTimestamp ?? first.Fact.Timestamp,
                parentHistoryView);
            if (baseline is { } value) childBaselines[group.Key] = value;
        }

        var output = new List<CodexParsedTurn>(turns.Count);
        foreach (var turn in turns)
        {
            if (turn.ForkedFromId is { Length: > 0 })
            {
                if (turn.SessionStartedAt is { } started && turn.Fact.Timestamp < started)
                    continue;
                var child = ChildIdentity(turn);
                if (!passedBaseline.Contains(child)
                    && childBaselines.TryGetValue(child, out var baseline))
                {
                    if (turn.CumulativeInput <= baseline.Input
                        && turn.CumulativeOutput <= baseline.Output)
                        continue;
                    passedBaseline.Add(child);
                }
            }

            output.Add(turn);
        }

        return new CodexReplayResult(output, passedBaseline);
    }

    public static string ChildIdentity(string? sessionId, string? sourcePath, string? fallbackRecordId = null) =>
        sessionId is { Length: > 0 }
            ? $"session:{sessionId}"
            : $"source:{sourcePath ?? fallbackRecordId ?? string.Empty}";

    public static (long Input, long Output)? BaselineFor(
        string? parentSessionId,
        DateTimeOffset? sessionStartedAt,
        DateTimeOffset? firstTurnTimestamp,
        IReadOnlyDictionary<string, IReadOnlyList<CodexUsageCheckpoint>> parentHistory)
    {
        if (string.IsNullOrEmpty(parentSessionId)
            || !parentHistory.TryGetValue(parentSessionId, out var checkpoints))
            return null;

        var forkAt = sessionStartedAt ?? firstTurnTimestamp;
        var baseline = forkAt is { } timestamp ? BaselineAt(checkpoints, timestamp) : null;

        // Some logs have second-level timestamps where the parent's final
        // pre-fork event and the child's first event tie exactly. Do not use a
        // later parent checkpoint: the parent may have continued independently.
        if (baseline is null
            && sessionStartedAt is not null
            && firstTurnTimestamp is { } firstTurn
            && checkpoints.Any(item => item.Timestamp == firstTurn))
            baseline = BaselineAt(checkpoints, firstTurn);

        return baseline;
    }

    private static string ChildIdentity(CodexParsedTurn turn) =>
        ChildIdentity(
            turn.Fact.Source.SessionId,
            turn.Fact.Source.SourcePath,
            turn.Fact.Source.RecordId);

    private static (long Input, long Output)? BaselineAt(
        IReadOnlyCollection<CodexUsageCheckpoint> checkpoints,
        DateTimeOffset timestamp)
    {
        var checkpoint = checkpoints
            .Where(item => item.Timestamp <= timestamp)
            .OrderByDescending(item => item.Timestamp)
            .ThenByDescending(item => item.Input)
            .ThenByDescending(item => item.Output)
            .FirstOrDefault();
        return checkpoint is null ? null : (checkpoint.Input, checkpoint.Output);
    }

    private static void AddCheckpoint(
        IDictionary<string, List<CodexUsageCheckpoint>> history,
        string session,
        CodexUsageCheckpoint checkpoint)
    {
        if (!history.TryGetValue(session, out var checkpoints))
        {
            checkpoints = new List<CodexUsageCheckpoint>();
            history[session] = checkpoints;
        }
        checkpoints.Add(checkpoint);
    }
}
