using System.IO;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Providers.Sessions;
using AgentIsland.Windows;
using AgentIsland.Windows.Storage;

namespace AgentIsland.Backend.Monitoring.Sensors;

public sealed class GrokSessionSensor : ISessionSensor
{
    private static string GrokSessionsRoot => Path.Combine(IslandPaths.Home, ".grok", "sessions");

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
        var output = new List<ScannedSession>();
        foreach (var projectDir in SafeFileSystem.EnumerateDirectories(GrokSessionsRoot))
        {
            var cwd = SessionScanner.DecodePathSegment(Path.GetFileName(projectDir));
            foreach (var sessionDir in SafeFileSystem.EnumerateDirectories(projectDir))
            {
                var summary = Path.Combine(sessionDir, "summary.json");
                if (!File.Exists(summary)) continue;
                var sid = Path.GetFileName(sessionDir);
                var title = GrokTitle(summary);
                var updates = Path.Combine(sessionDir, "updates.jsonl");
                var transcript = File.Exists(updates)
                    ? updates
                    : Path.Combine(sessionDir, "chat_history.jsonl");
                var state = SessionScanner.SessionState(transcript, now, lastWorking, null, SessionTurnState.Grok);
                output.Add(new ScannedSession(
                    TriggerTool.Grok,
                    sid,
                    cwd,
                    string.IsNullOrEmpty(title) ? SessionScanner.Fallback(cwd, sid) : title,
                    state.Modified,
                    state.Status,
                    transcript,
                    state.TurnKey,
                    SessionLaunchTarget.Cli));
            }
        }
        return output;
    }

    public static string GrokTitle(string summaryPath)
    {
        try
        {
            using var stream = SessionScanner.OpenShared(summaryPath);
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            return (Jsonl.GetString(doc.RootElement, "session_summary") ?? "").Trim();
        }
        catch
        {
            return "";
        }
    }
}
