using System.IO;
using System.Text;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Providers.Sessions;
using AgentIsland.Windows;
using AgentIsland.Windows.Storage;

namespace AgentIsland.Backend.Monitoring.Sensors;

public sealed class AntigravitySessionSensor : ISessionSensor
{
    private static readonly string[] AntigravityRootNames = { "antigravity", "antigravity-ide", "antigravity-cli" };

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
        var home = Path.Combine(IslandPaths.Home, ".gemini");
        foreach (var rootName in AntigravityRootNames)
        {
            var root = Path.Combine(home, rootName);
            var brain = Path.Combine(root, "brain");
            if (!Directory.Exists(brain)) continue;
            var summaries = AntigravitySummaries(root);
            foreach (var conversationDir in SafeFileSystem.EnumerateDirectories(brain))
            {
                var conversation = Path.GetFileName(conversationDir);
                var path = Path.Combine(
                    conversationDir, ".system_generated", "logs", "transcript_full.jsonl");
                if (!File.Exists(path)) continue;
                var state = SessionScanner.SessionState(
                    path, now, lastWorking, null, SessionTurnState.Antigravity, quietMeansDone: true);
                var summary = summaries.TryGetValue(conversation, out var s) ? s : default;
                var cwd = summary.Workspace
                    ?? AntigravityWorkspace(root, conversation) ?? "";
                var label = summary.Title
                    ?? AntigravityTitle(path)
                    ?? (conversation.Length > 8 ? conversation[..8] : conversation);
                output.Add(new ScannedSession(
                    TriggerTool.Antigravity,
                    conversation,
                    cwd,
                    label,
                    state.Modified,
                    state.Status,
                    path,
                    state.TurnKey,
                    SessionLaunchTarget.Cli));
            }
        }
        return output;
    }

    public static Dictionary<string, (string? Title, string? Workspace)> AntigravitySummaries(string root)
    {
        var dbPath = Path.Combine(root, "conversation_summaries.db");
        if (!File.Exists(dbPath)) return new();
        return AntigravitySummaryRows($"Data Source={dbPath};Mode=ReadOnly")
            ?? AntigravitySummaryRows(
                $"Data Source=file:{Uri.EscapeDataString(dbPath).Replace("%5C", "/").Replace("%3A", ":")}?immutable=1;Mode=ReadOnly")
            ?? new();
    }

    private static Dictionary<string, (string? Title, string? Workspace)>? AntigravitySummaryRows(
        string connectionString)
    {
        try
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT conversation_id, title, preview, workspace_uris FROM conversation_summaries";
            using var reader = command.ExecuteReader();
            var output = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
            while (reader.Read())
            {
                var id = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (string.IsNullOrEmpty(id)) continue;
                var title = NonEmpty(reader.IsDBNull(1) ? null : reader.GetString(1))
                    ?? NonEmpty(reader.IsDBNull(2) ? null : reader.GetString(2));
                if (title is { Length: > 48 }) title = title[..48];
                var workspace = NonEmpty(reader.IsDBNull(3) ? null : reader.GetString(3)) is { } uris
                    ? AntigravityWorkspaceUri(uris)
                    : null;
                output[id] = (title, workspace);
            }
            return output;
        }
        catch
        {
            return null;
        }

        static string? NonEmpty(string? raw)
        {
            var trimmed = raw?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }
    }

    private static string? AntigravityWorkspaceUri(string raw)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var text = entry.GetString();
                if (string.IsNullOrEmpty(text)) continue;
                if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsFile)
                {
                    return uri.LocalPath;
                }
                return text;
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    public static string? AntigravityTitle(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            for (var i = 0; i < 40 && reader.ReadLine() is { } line; i++)
            {
                using var doc = Jsonl.TryParseLine(line);
                if (doc is null) continue;
                if (Jsonl.GetString(doc.RootElement, "type") != "USER_INPUT") continue;
                if (Jsonl.GetString(doc.RootElement, "content") is not { Length: > 0 } content) continue;
                return AntigravityRequestText(content);
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    public static string? AntigravityRequestText(string content)
    {
        var body = content;
        var start = content.IndexOf("<USER_REQUEST>", StringComparison.Ordinal);
        var end = content.IndexOf("</USER_REQUEST>", StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            body = content[(start + "<USER_REQUEST>".Length)..end];
        }
        var first = body.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
        if (first.Length == 0) return null;
        return first.Length > 48 ? first[..48] : first;
    }

    public static string? AntigravityWorkspace(string root, string conversation)
    {
        try
        {
            var historyPath = Path.Combine(root, "history.jsonl");
            if (!File.Exists(historyPath)) return null;
            foreach (var line in File.ReadLines(historyPath))
            {
                using var doc = Jsonl.TryParseLine(line);
                if (doc is null) continue;
                if (Jsonl.GetString(doc.RootElement, "conversationId") != conversation) continue;
                if (Jsonl.GetString(doc.RootElement, "workspace") is { Length: > 0 } workspace)
                {
                    return workspace;
                }
            }
        }
        catch
        {
            return null;
        }
        return null;
    }
}
