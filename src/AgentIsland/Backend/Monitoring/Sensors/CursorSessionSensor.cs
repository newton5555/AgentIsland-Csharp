using System.IO;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Providers.Sessions;
using AgentIsland.Windows;
using AgentIsland.Windows.Storage;

namespace AgentIsland.Backend.Monitoring.Sensors;

public sealed class CursorSessionSensor : ISessionSensor
{
    private const double GuestQuietAfterSeconds = 25;
    private static readonly TimeSpan NeedsYouCap = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StallCap = TimeSpan.FromMinutes(15);

    private static readonly object CursorGate = new();
    private static DateTimeOffset _cursorStamp = DateTimeOffset.MinValue;
    private static List<CursorConversation> _cursorCache = new();

    public readonly record struct CursorConversation(
        string Id, string Label, bool IsDone, string? TurnKey, DateTimeOffset Stamp);

    public ValueTask<IReadOnlyList<ScannedSession>> ScanSessionsAsync(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        CancellationToken ct = default)
    {
        return ValueTask.FromResult<IReadOnlyList<ScannedSession>>(Scan(now, lastWorking));
    }

    public static List<ScannedSession> Scan(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        var path = IslandPaths.CursorGlobalStorageDatabase;
        if (!File.Exists(path)) return new List<ScannedSession>();
        var modified = SafeFileSystem.LastWriteTime(path);

        List<CursorConversation>? cached = null;
        lock (CursorGate)
        {
            if (modified == _cursorStamp) cached = _cursorCache;
        }
        if (cached is not null)
        {
            return cached.ConvertAll(c => CursorSession(c, now, lastWorking));
        }

        var parsed = new List<CursorConversation>();
        foreach (var (composerId, json) in CursorConversations.NewestBubblePerConversation(path))
        {
            var turn = SessionTurnState.Cursor(new[] { json });
            if (turn.ActivityDate is not { } stamp) continue;
            parsed.Add(new CursorConversation(
                composerId,
                CursorConversations.Title(json) ?? composerId[..Math.Min(8, composerId.Length)],
                turn.IsDone,
                turn.Key,
                stamp));
        }
        parsed.Sort((a, b) => b.Stamp.CompareTo(a.Stamp));

        lock (CursorGate)
        {
            _cursorStamp = modified;
            _cursorCache = parsed;
        }
        return parsed.ConvertAll(c => CursorSession(c, now, lastWorking));
    }

    public static void ClearCache()
    {
        lock (CursorGate)
        {
            _cursorStamp = DateTimeOffset.MinValue;
            _cursorCache.Clear();
        }
    }

    private static ScannedSession CursorSession(
        CursorConversation conversation,
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        var key = "cursor:" + conversation.Id;
        var turn = new SessionTurnStatus(conversation.IsDone, conversation.TurnKey, conversation.Stamp);
        return new ScannedSession(
            TriggerTool.Cursor,
            conversation.Id,
            string.Empty,
            conversation.Label,
            conversation.Stamp,
            CursorStatus(turn, conversation.Stamp, now, key, lastWorking),
            key,
            conversation.TurnKey,
            SessionLaunchTarget.Cli);
    }

    private static ActivityState CursorStatus(
        SessionTurnStatus turn, DateTimeOffset stamp, DateTimeOffset now,
        string key, IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        var age = now - stamp;
        if (turn.IsDone)
        {
            if (age.TotalSeconds <= GuestQuietAfterSeconds) return ActivityState.Working;
            return age < NeedsYouCap ? ActivityState.NeedsYou : ActivityState.Idle;
        }
        if (age < StallAfter) return ActivityState.Working;
        if (lastWorking.TryGetValue(key, out var seen)
            && (now - seen) < StallCap && age < StallCap)
        {
            return ActivityState.Stalled;
        }
        return ActivityState.Idle;
    }
}
