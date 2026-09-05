using System.IO;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Providers.Sessions;
using AgentIsland.Windows;
using AgentIsland.Windows.Storage;

namespace AgentIsland.Backend.Monitoring.Sensors;

public sealed class ClaudeSessionSensor : ISessionSensor
{
    public ValueTask<IReadOnlyList<ScannedSession>> ScanSessionsAsync(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        CancellationToken ct = default)
    {
        return ValueTask.FromResult<IReadOnlyList<ScannedSession>>(ScanClaudeTranscripts(now, lastWorking, excludeArchived: false));
    }

    public static List<ScannedSession> ScanClaudeFromDesktopStore(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        var output = new List<ScannedSession>();
        var transcripts = ClaudeTranscriptIndex();
        foreach (var entry in EnumerateDesktopSessionFiles())
        {
            var parsed = ParseDesktopSessionFile(entry);
            if (parsed is not { } session) continue;
            if (session.IsArchived) continue;
            if (string.IsNullOrEmpty(session.CliSessionId)) continue;
            transcripts.TryGetValue(session.CliSessionId, out var transcript);
            var state = SessionScanner.SessionState(
                transcript,
                now,
                lastWorking,
                session.LastActivityAt,
                SessionTurnState.Claude);
            output.Add(new ScannedSession(
                TriggerTool.Claude,
                session.CliSessionId,
                session.Cwd,
                string.IsNullOrEmpty(session.Title) ? SessionScanner.Fallback(session.Cwd, session.CliSessionId) : session.Title,
                state.Modified,
                state.Status,
                transcript,
                state.TurnKey,
                SessionLaunchTarget.ClaudeDesktop));
        }
        return output;
    }

    public static List<ScannedSession> ScanClaudeTranscripts(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        bool excludeArchived = false)
    {
        var desktopSessions = ClaudeDesktopIndex();
        var output = new List<ScannedSession>();
        foreach (var (sid, path) in ClaudeTranscriptIndex())
        {
            desktopSessions.TryGetValue(sid, out var desktop);
            if (excludeArchived && desktop is { IsArchived: true }) continue;
            var cwd = desktop?.Cwd is { Length: > 0 } dc ? dc : CwdFromClaudeTranscript(path);
            var title = desktop?.Title ?? "";
            var state = SessionScanner.SessionState(
                path,
                now,
                lastWorking,
                desktop?.LastActivityAt,
                SessionTurnState.Claude);
            output.Add(new ScannedSession(
                TriggerTool.Claude,
                sid,
                cwd,
                string.IsNullOrEmpty(title) ? SessionScanner.Fallback(cwd, sid) : title,
                state.Modified,
                state.Status,
                path,
                state.TurnKey,
                desktop is null ? SessionLaunchTarget.Cli : SessionLaunchTarget.ClaudeDesktop));
        }
        return output;
    }

    public static bool IsClaudeSubagentTranscript(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Contains("subagents", StringComparer.OrdinalIgnoreCase)) return true;
        return segments[^1].StartsWith("agent-", StringComparison.OrdinalIgnoreCase);
    }

    public static string CwdFromClaudeTranscript(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            for (var i = 0; i < 30 && reader.ReadLine() is { } line; i++)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("cwd", out var cwd)
                        && cwd.ValueKind == System.Text.Json.JsonValueKind.String
                        && cwd.GetString() is { Length: > 0 } value)
                    {
                        return value;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Non-JSON noise before the session header — skip and keep looking.
                }
            }
        }
        catch
        {
            // Unreadable transcript — the caller's fallback will step in.
        }
        return ProjectFromClaudeTranscript(path);
    }

    public static string ProjectFromClaudeTranscript(string path)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        if (parent.Length == 0) return "";
        if (parent.Length > 3 && char.IsAsciiLetter(parent[0]) && parent[1] == '-' && parent[2] == '-')
        {
            return parent[0] + ":\\" + parent[3..].Replace('-', '\\');
        }
        if (parent.StartsWith("--", StringComparison.Ordinal))
        {
            return @"\\" + parent[2..].Replace('-', '\\');
        }
        return parent.Replace('-', '\\');
    }


    public static Dictionary<string, string> ClaudeTranscriptIndex()
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in IslandPaths.ClaudeProjectRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var path in SafeFileSystem.EnumerateFiles(root, "*.jsonl"))
            {
                if (IsClaudeSubagentTranscript(Path.GetRelativePath(root, path)))
                {
                    continue;
                }
                var sid = Path.GetFileNameWithoutExtension(path);
                output.TryAdd(sid, path);
            }
        }
        return output;
    }

    public sealed record DesktopSession(
        string CliSessionId, string Title, string Cwd, bool IsArchived, DateTimeOffset? LastActivityAt);

    public static IEnumerable<string> EnumerateDesktopSessionFiles()
    {
        var root = IslandPaths.ClaudeDesktopSessionsRoot;
        if (!Directory.Exists(root)) return Array.Empty<string>();
        return SafeFileSystem.EnumerateFiles(root, "local_*.json");
    }

    public static DesktopSession? ParseDesktopSessionFile(string path)
    {
        try
        {
            using var stream = SessionScanner.OpenShared(path);
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var cliSessionId = Jsonl.GetString(root, "cliSessionId") ?? "";
            var ms = Jsonl.GetDouble(root, "lastActivityAt") ?? Jsonl.GetDouble(root, "createdAt");
            return new DesktopSession(
                cliSessionId,
                Jsonl.GetString(root, "title") ?? "",
                Jsonl.GetString(root, "cwd") ?? "",
                Jsonl.GetBool(root, "isArchived") ?? false,
                ms is { } m ? DateTimeOffset.FromUnixTimeMilliseconds((long)m) : null);
        }
        catch
        {
            return null;
        }
    }

    public static Dictionary<string, DesktopSession> ClaudeDesktopIndex()
    {
        var output = new Dictionary<string, DesktopSession>(StringComparer.Ordinal);
        foreach (var path in EnumerateDesktopSessionFiles())
        {
            if (ParseDesktopSessionFile(path) is not { } session) continue;
            if (string.IsNullOrEmpty(session.CliSessionId)) continue;
            output[session.CliSessionId] = session;
        }
        return output;
    }
}
