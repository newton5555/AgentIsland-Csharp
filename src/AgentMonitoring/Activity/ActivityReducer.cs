using AgentIsland.Core;
using AgentIsland.Core.Agents;

namespace AgentMonitoring.Activity;

/// Picks one visible state per AgentKey. Unknown keys are first-class: the
/// reducer never requires DisplayProvider or TriggerTool.
public static class ActivityReducer
{
    public static AgentActivity Reduce(
        AgentKey agent,
        IEnumerable<ScannedSession> sessions,
        Func<ActivityThread, bool> isAcknowledged,
        DateTimeOffset now)
    {
        var mine = sessions.Where(session => session.AgentKey.Value == agent.Value).ToList();
        var needsYou = mine
            .Where(session => session.Status == ActivityState.NeedsYou)
            .OrderByDescending(session => session.Modified)
            .Select(ToThread)
            .ToList();

        var ranked = mine
            .Select(session => (Session: session, Priority: Priority(session, isAcknowledged)))
            .OrderByDescending(item => item.Priority)
            .ThenByDescending(item => item.Session.Modified)
            .ToList();

        if (ranked.Count == 0)
            return new AgentActivity(agent, ActivityState.Idle, null, needsYou, now);

        var top = ranked[0].Session;
        var thread = top.Status == ActivityState.Idle ? null : ToThread(top);
        return new AgentActivity(agent, top.Status, thread, needsYou, now);
    }

    public static ActivityThread ToThread(ScannedSession session) => new(
        session.SessionId,
        session.Label,
        session.Cwd,
        session.Modified,
        session.TranscriptPath,
        session.TurnKey,
        session.LaunchTarget);

    private static int Priority(ScannedSession session, Func<ActivityThread, bool> isAcknowledged) =>
        session.Status switch
        {
            ActivityState.Stalled => 4,
            ActivityState.Working => 3,
            ActivityState.NeedsYou => isAcknowledged(ToThread(session)) ? 1 : 2,
            _ => 0,
        };
}
