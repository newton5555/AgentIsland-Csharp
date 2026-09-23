namespace AgentIsland.Providers.Cost.Codex;

public sealed record CodexUsageCheckpoint(DateTimeOffset Timestamp, long Input, long Output);

/// Drops inherited parent-history prefixes from forked Codex sessions.
public static class CodexReplayPlan
{
    public static List<CodexParsedTurn> Apply(
        IReadOnlyList<CodexParsedTurn> turns,
        IReadOnlyDictionary<string, IReadOnlyList<CodexUsageCheckpoint>>? knownParentHistory = null)
    {
        if (turns.Count == 0) return new List<CodexParsedTurn>();

        var parentHistory = new Dictionary<string, List<CodexUsageCheckpoint>>(StringComparer.Ordinal);
        if (knownParentHistory is not null)
        {
            foreach (var (session, checkpoints) in knownParentHistory)
            {
                if (string.IsNullOrEmpty(session)) continue;
                parentHistory[session] = checkpoints.ToList();
            }
        }

        foreach (var turn in turns)
        {
            var session = turn.Fact.Source.SessionId;
            if (session is null || turn.ForkedFromId is not null) continue;
            AddCheckpoint(parentHistory, session,
                new CodexUsageCheckpoint(turn.Fact.Timestamp, turn.CumulativeInput, turn.CumulativeOutput));
        }

        var childBaselines = new Dictionary<string, (long Input, long Output)>(StringComparer.Ordinal);
        foreach (var group in turns
            .Where(turn => turn.ForkedFromId is { Length: > 0 })
            .GroupBy(ChildIdentity, StringComparer.Ordinal))
        {
            var first = group.OrderBy(turn => turn.Fact.Timestamp).First();
            var parent = first.ForkedFromId!;
            if (!parentHistory.TryGetValue(parent, out var checkpoints)) continue;

            var forkAt = first.SessionStartedAt ?? first.Fact.Timestamp;
            var baseline = BaselineAt(checkpoints, forkAt);

            // Some logs have second-level timestamps where the parent's final
            // pre-fork event and the child's first replayed event tie exactly.
            // Do not use a later parent checkpoint: the parent may have continued
            // independently after the fork.
            if (baseline is null
                && first.SessionStartedAt is not null
                && checkpoints.Any(item => item.Timestamp == first.Fact.Timestamp))
                baseline = BaselineAt(checkpoints, first.Fact.Timestamp);

            if (baseline is { } value) childBaselines[group.Key] = value;
        }

        var output = new List<CodexParsedTurn>(turns.Count);
        var passedBaseline = new HashSet<string>(StringComparer.Ordinal);
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

        return output;
    }

    private static string ChildIdentity(CodexParsedTurn turn) =>
        turn.Fact.Source.SessionId is { Length: > 0 } session
            ? $"session:{session}"
            : $"source:{turn.Fact.Source.SourcePath ?? turn.Fact.Source.RecordId}";

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
