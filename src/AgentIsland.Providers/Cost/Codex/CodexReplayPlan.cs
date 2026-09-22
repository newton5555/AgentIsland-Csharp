namespace AgentIsland.Providers.Cost.Codex;

/// Drops inherited parent-history prefixes from forked Codex sessions.
public static class CodexReplayPlan
{
    public static List<CodexParsedTurn> Apply(
        IReadOnlyList<CodexParsedTurn> turns,
        IReadOnlyDictionary<string, (long Input, long Output)>? knownParentTotals = null)
    {
        if (turns.Count == 0) return new List<CodexParsedTurn>();

        var parentTotals = new Dictionary<string, (long Input, long Output)>(StringComparer.Ordinal);
        if (knownParentTotals is not null)
        {
            foreach (var (session, totals) in knownParentTotals)
            {
                if (string.IsNullOrEmpty(session)) continue;
                parentTotals[session] = totals;
            }
        }

        foreach (var turn in turns)
        {
            var session = turn.Fact.Source.SessionId;
            if (session is null || turn.ForkedFromId is not null) continue;
            if (!parentTotals.TryGetValue(session, out var current)
                || turn.CumulativeInput > current.Input
                || turn.CumulativeOutput > current.Output)
            {
                parentTotals[session] = (turn.CumulativeInput, turn.CumulativeOutput);
            }
        }

        var output = new List<CodexParsedTurn>(turns.Count);
        foreach (var turn in turns)
        {
            if (turn.ForkedFromId is { Length: > 0 } parent)
            {
                if (turn.SessionStartedAt is { } started && turn.Fact.Timestamp < started)
                    continue;
                if (parentTotals.TryGetValue(parent, out var baseline)
                    && turn.CumulativeInput <= baseline.Input
                    && turn.CumulativeOutput <= baseline.Output)
                {
                    continue;
                }
            }

            output.Add(turn);
        }

        return output;
    }
}
