using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentMonitoring.Activity;

namespace AgentMonitoring.Notifications;

public enum ReminderKind
{
    NeedsYou = 0,
}

public sealed record ReminderEvent(
    string DeliveryKey,
    AgentKey Agent,
    ActivityThread Thread,
    ReminderKind Kind,
    DateTimeOffset RaisedAt);

public sealed record ReminderDiff(
    IReadOnlyList<ReminderEvent> Raised,
    IReadOnlyList<string> Dismissed);

public interface IReminderSink
{
    void Deliver(ReminderEvent reminder);
    void Dismiss(string deliveryKey);
    void Observe(AgentKey agent) { }
}

/// Dedup and acknowledge live in the monitoring layer so a rebuilt window
/// cannot replay the same turn. Desktop code only delivers or dismisses.
public sealed class ReminderBroker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _acknowledged = new(StringComparer.Ordinal);

    public bool HasAcknowledged(string deliveryKey)
    {
        lock (_gate) return _acknowledged.ContainsKey(deliveryKey);
    }

    public void Acknowledge(string deliveryKey)
    {
        lock (_gate) _acknowledged[deliveryKey] = DateTimeOffset.UtcNow;
    }

    public ReminderDiff Diff(AgentKey agent, IReadOnlyList<ActivityThread> needsYouThreads)
    {
        var raised = new List<ReminderEvent>();
        var dismissed = new List<string>();
        lock (_gate)
        {
            var current = needsYouThreads
                .Select(thread => (Key: ReminderKeys.ForNeedsYou(agent, thread), Thread: thread))
                .ToList();
            var currentKeys = current.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            if (_active.TryGetValue(agent.Value, out var previous))
            {
                foreach (var stale in previous.Where(key => !currentKeys.Contains(key)))
                    dismissed.Add(stale);
            }

            foreach (var (key, thread) in current)
            {
                if (_acknowledged.ContainsKey(key)) continue;
                if (previous is not null && previous.Contains(key)) continue;
                raised.Add(new ReminderEvent(key, agent, thread, ReminderKind.NeedsYou, DateTimeOffset.UtcNow));
            }

            _active[agent.Value] = currentKeys;
        }

        return new ReminderDiff(raised, dismissed);
    }
}

public static class ReminderKeys
{
    public static string ForNeedsYou(AgentKey agent, ActivityThread thread)
    {
        var threadKey = !string.IsNullOrEmpty(thread.TranscriptPath) ? thread.TranscriptPath
            : !string.IsNullOrEmpty(thread.SessionId) ? thread.SessionId
            : !string.IsNullOrEmpty(thread.Cwd) ? $"{thread.Cwd}:{thread.Label}"
            : thread.Label;
        var turn = string.IsNullOrEmpty(thread.TurnKey) ? "latest" : thread.TurnKey;
        return $"{agent.Value}-{(int)ActivityState.NeedsYou}-{threadKey}-{turn}";
    }
}
