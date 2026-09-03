using System.IO;
using System.Text;

namespace AgentIsland.Core;

/// Discovers Claude Code / Claude Desktop / Codex / Grok / Gemini sessions
/// from the local artifacts those tools already write, and classifies each one
/// through a conservative state machine. Direct port of the macOS
/// SessionScanner; the thresholds are the tuned values from the shipping app.
///
/// Cursor is absent on purpose: its conversation-search.db is a batch search
/// cache rather than a live stream, so it cannot answer "is this session
/// running right now" honestly.
public static class SessionScanner
{
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StallCap = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan NeedsYouCap = TimeSpan.FromMinutes(20);
    /// How long a guest transcript must sit unchanged, after visible
    /// activity, before quiet counts as turn-done.
    private const double GuestQuietAfterSeconds = 25;
    private static readonly TimeSpan AttentionWindow = TimeSpan.FromMinutes(30);
    /// Claude Desktop writes lastActivityAt 2.3-4.2s after the final assistant
    /// event as turn-completion bookkeeping; external activity inside this
    /// window must not suppress needsYou.
    private static readonly TimeSpan DesktopBookkeepingGrace = TimeSpan.FromSeconds(25);

    private const int MonitoringCodexLimit = 120;

    private static string GrokSessionsRoot => Path.Combine(IslandPaths.Home, ".grok", "sessions");
    /// Google has renamed the data directory twice already (1.x
    /// `antigravity`, 2.x `antigravity-ide`, plus the separate
    /// `antigravity-cli` root), so every known variant is probed.
    private static readonly string[] AntigravityRootNames = { "antigravity", "antigravity-ide", "antigravity-cli" };

    // MARK: - Entry points

    /// Picker scan: desktop-titled Claude threads (archived filtered) UNION
    /// transcript-only CLI threads the desktop store has never seen, one Codex
    /// entry per project, plus every Grok and Gemini session.
    public static List<ScannedSession> Scan(DateTimeOffset now, IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        var output = ScanClaudeFromDesktopStore(now, lastWorking);
        var known = new HashSet<string>(output.Select(s => s.SessionId), StringComparer.Ordinal);
        output.AddRange(ScanClaudeTranscripts(now, lastWorking, excludeArchived: true)
            .Where(s => !known.Contains(s.SessionId)));
        output.AddRange(ScanCodex(now, lastWorking, limit: 30, dedupeProjects: true));
        output.AddRange(ScanGrok(now, lastWorking));
        output.AddRange(ScanAntigravity(now, lastWorking));
        output.AddRange(ScanCursor(now, lastWorking));
        output.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        // Dedupe by session: the Claude Desktop store commonly holds the SAME
        // cliSessionId under two project folders (23 of 41 on the reporting
        // machine), so a raw file scan lists every such session twice in the
        // picker. Sorted newest-first, keep the first sighting of each
        // (tool, sessionId).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        output.RemoveAll(session => !seen.Add(session.Id));
        return output;
    }

    /// Monitoring scan: every Claude transcript (desktop-labelled when known),
    /// every recent Codex rollout with no project dedupe, plus every Grok and
    /// Gemini session. Subagent / child threads never participate — machine
    /// fan-out finishes dozens of threads per prompt and a human is never "up"
    /// in any of them, so they are skipped outright rather than gated behind a
    /// toggle (owner call, 2026-08-08).
    public static List<ScannedSession> MonitoringScan(DateTimeOffset now, IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        var output = ScanClaudeTranscripts(now, lastWorking, excludeArchived: false);
        output.AddRange(ScanCodex(now, lastWorking, limit: MonitoringCodexLimit, dedupeProjects: false));
        output.AddRange(ScanGrok(now, lastWorking));
        output.AddRange(ScanAntigravity(now, lastWorking));
        output.AddRange(ScanCursor(now, lastWorking));
        output.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        return output;
    }

    // MARK: - Claude: desktop session store (titles + archived flag)

    private static List<ScannedSession> ScanClaudeFromDesktopStore(
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
            var state = SessionState(
                transcript,
                now,
                lastWorking,
                session.LastActivityAt,
                SessionTurnState.Claude);
            output.Add(new ScannedSession(
                TriggerTool.Claude,
                session.CliSessionId,
                session.Cwd,
                string.IsNullOrEmpty(session.Title) ? Fallback(session.Cwd, session.CliSessionId) : session.Title,
                state.Modified,
                state.Status,
                transcript,
                state.TurnKey,
                SessionLaunchTarget.ClaudeDesktop));
        }
        return output;
    }

    // MARK: - Claude: all transcripts (monitoring)

    private static List<ScannedSession> ScanClaudeTranscripts(
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
            var state = SessionState(
                path,
                now,
                lastWorking,
                desktop?.LastActivityAt,
                SessionTurnState.Claude);
            output.Add(new ScannedSession(
                TriggerTool.Claude,
                sid,
                cwd,
                string.IsNullOrEmpty(title) ? Fallback(cwd, sid) : title,
                state.Modified,
                state.Status,
                path,
                state.TurnKey,
                desktop is null ? SessionLaunchTarget.Cli : SessionLaunchTarget.ClaudeDesktop));
        }
        return output;
    }

    /// True when a transcript belongs to an orchestrated subagent rather
    /// than a user conversation: nested under a subagents/ directory (the
    /// current layout) or named agent-*.jsonl (flat layouts). Main session
    /// files are always UUID-named.
    internal static bool IsClaudeSubagentTranscript(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Contains("subagents", StringComparer.OrdinalIgnoreCase)) return true;
        return segments[^1].StartsWith("agent-", StringComparison.OrdinalIgnoreCase);
    }

    // MARK: - Codex: %USERPROFILE%\.codex\sessions (CODEX_HOME overrides)

    public static List<ScannedSession> ScanCodex(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        int limit = 30,
        bool dedupeProjects = true)
    {
        var root = IslandPaths.CodexSessionsRoot;
        if (!Directory.Exists(root)) return new List<ScannedSession>();

        var files = SafeEnumerateFiles(root, "*.jsonl");
        // Precompute mtimes once — calling Mtime() inside the comparator issues
        // O(n log n) GetLastWriteTime syscalls over a full recursive tree.
        var mtimes = new Dictionary<string, DateTimeOffset>(files.Count, StringComparer.Ordinal);
        foreach (var f in files) mtimes[f] = Mtime(f);
        files.Sort((a, b) => mtimes[b].CompareTo(mtimes[a]));

        var titles = CodexTitleIndex();
        var output = new List<ScannedSession>();
        var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            if (CodexMeta(path) is not { } meta || string.IsNullOrEmpty(meta.Sid)) continue;
            var (sid, cwd, kind) = meta;
            // Neither machine tier ever surfaces: automation runs have no
            // human in them at all, and subagent fan-out finishes constantly
            // without anyone's turn coming up.
            if (kind is CodexRolloutKind.Automation or CodexRolloutKind.Subagent) continue;
            var projectKey = string.IsNullOrEmpty(cwd) ? sid : cwd;
            if (dedupeProjects && !seenProjects.Add(projectKey)) continue;
            var state = SessionState(path, now, lastWorking, null, SessionTurnState.Codex);
            output.Add(new ScannedSession(
                TriggerTool.Codex,
                sid,
                cwd,
                titles.TryGetValue(sid, out var title) ? title : Fallback(cwd, sid),
                state.Modified,
                state.Status,
                path,
                state.TurnKey,
                SessionLaunchTarget.Cli));
            if (output.Count >= limit) break;
        }
        return output;
    }

    internal enum CodexRolloutKind
    {
        Interactive,
        Subagent,
        Automation,
    }

    /// Reads the first JSONL line in full. Codex's `session_meta` is line 1
    /// but can be tens of KB (it embeds the full base instructions), so keep
    /// pulling chunks until the first newline.
    private static (string Sid, string Cwd, CodexRolloutKind Kind)? CodexMeta(string path)
    {
        byte[] firstLine;
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var buffer = new List<byte>(65_536);
            var chunk = new byte[65_536];
            var newline = -1;
            while (newline < 0)
            {
                var read = stream.Read(chunk, 0, chunk.Length);
                if (read <= 0) break;
                for (var i = 0; i < read && newline < 0; i++)
                {
                    if (chunk[i] == (byte)'\n') newline = buffer.Count + i;
                }
                buffer.AddRange(chunk.AsSpan(0, read).ToArray());
                if (buffer.Count > 2_000_000) break;
            }
            firstLine = newline >= 0 ? buffer.Take(newline).ToArray() : buffer.ToArray();
        }
        catch
        {
            return null;
        }
        return ParseCodexMeta(Encoding.UTF8.GetString(firstLine));
    }

    /// Classifies a rollout from its session_meta line. `payload.source` is
    /// a plain string for direct sessions ("cli", "vscode", "exec", "mcp")
    /// and an object for machine-driven ones: {"subagent": …} for spawned /
    /// review / compact threads, {"internal": …} for probes. Subagent
    /// threads share the interactive codex originator, so the source field
    /// is the only thing separating a fan-out worker from the user's own
    /// thread — without it every finished subagent raises a turn alarm.
    internal static (string Sid, string Cwd, CodexRolloutKind Kind)? ParseCodexMeta(string firstLine)
    {
        using var doc = Jsonl.TryParseLine(firstLine);
        if (doc is null) return null;
        var root = doc.RootElement;
        if (Jsonl.GetString(root, "type") != "session_meta") return null;
        if (Jsonl.GetObject(root, "payload") is not { } payload) return null;

        var kind = CodexRolloutKind.Interactive;
        if (payload.TryGetProperty("source", out var source))
        {
            if (source.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                if (source.GetString() is "exec" or "mcp") kind = CodexRolloutKind.Automation;
            }
            else if (source.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (source.TryGetProperty("subagent", out _)) kind = CodexRolloutKind.Subagent;
                else if (source.TryGetProperty("internal", out _)) kind = CodexRolloutKind.Automation;
            }
        }

        // Automation rollouts (orchestrator-driven runs, probes, and
        // `codex exec`) finish constantly; a human is never "up" in them,
        // so they must not raise turn alarms or drive the logo. Interactive
        // sessions carry a codex-family originator; missing originator =
        // old CLI, treat as interactive. The prefix check is
        // case-insensitive: the Windows desktop app stamps "Codex Desktop".
        var originator = Jsonl.GetString(payload, "originator") ?? "";
        if (originator.Length > 0
            && (!originator.StartsWith("codex", StringComparison.OrdinalIgnoreCase)
                || string.Equals(originator, "codex_exec", StringComparison.OrdinalIgnoreCase)))
        {
            kind = CodexRolloutKind.Automation;
        }

        return (Jsonl.GetString(payload, "id") ?? "", Jsonl.GetString(payload, "cwd") ?? "", kind);
    }

    // MARK: - Grok: %USERPROFILE%\.grok\sessions\<url-encoded cwd>\<uuid>\

    /// Grok mirrors Claude's layout almost exactly — one directory per
    /// percent-encoded cwd, one directory per session inside it.
    /// `summary.json` carries identity + title; `updates.jsonl` is the live
    /// event stream the turn detector reads, with `chat_history.jsonl` as the
    /// fallback for sessions that predate the updates stream.
    public static List<ScannedSession> ScanGrok(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        var output = new List<ScannedSession>();
        foreach (var projectDir in SafeEnumerateDirectories(GrokSessionsRoot))
        {
            var cwd = DecodePathSegment(Path.GetFileName(projectDir));
            foreach (var sessionDir in SafeEnumerateDirectories(projectDir))
            {
                var summary = Path.Combine(sessionDir, "summary.json");
                if (!File.Exists(summary)) continue;
                var sid = Path.GetFileName(sessionDir);
                var title = GrokTitle(summary);
                var updates = Path.Combine(sessionDir, "updates.jsonl");
                var transcript = File.Exists(updates)
                    ? updates
                    : Path.Combine(sessionDir, "chat_history.jsonl");
                var state = SessionState(transcript, now, lastWorking, null, SessionTurnState.Grok);
                output.Add(new ScannedSession(
                    TriggerTool.Grok,
                    sid,
                    cwd,
                    string.IsNullOrEmpty(title) ? Fallback(cwd, sid) : title,
                    state.Modified,
                    state.Status,
                    transcript,
                    state.TurnKey,
                    SessionLaunchTarget.Cli));
            }
        }
        return output;
    }

    private static string GrokTitle(string summaryPath)
    {
        try
        {
            using var stream = OpenShared(summaryPath);
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            return (Jsonl.GetString(doc.RootElement, "session_summary") ?? "").Trim();
        }
        catch
        {
            return "";
        }
    }

    // MARK: - Gemini: %USERPROFILE%\.gemini\tmp\<project>\chats\session-*.jsonl

    /// Gemini's chat files are a `$set` checkpoint stream with no verified
    /// turn boundary, so status is recency-only (MtimeOnly — working while
    /// the file moves, idle after, never a "your turn" alarm). The session id
    /// lives in the first line's header; the filename stem is the fallback.
    /// Cursor's conversation-search.db is a batch cache and can not drive
    /// live status; what does move in real time is each workspace's
    /// state.vscdb (+ -wal journal) under
    /// %APPDATA%\Cursor\User\workspaceStorage. That is an honest recency
    /// signal — working / idle, no turn boundary — so like Gemini it rides
    /// MtimeOnly and never claims "your turn".
    /// Cursor conversations, read from the same globalStorage db the cost
    /// reader opens through winsqlite3. Rows keyed `composerData:&lt;id&gt;` are
    /// the conversations; `bubbleId:&lt;composerId&gt;:&lt;bubbleId&gt;` rows are the
    /// messages, and a bubble's `type` is a REAL turn boundary — 1 user,
    /// 2 assistant. Same rule Claude gets: assistant spoke last and has gone
    /// quiet means it is your turn.
    ///
    /// Skipped entirely when the db has not been written since the last
    /// scan — this runs every 6 s against a file that can be hundreds of MB.
    public static List<ScannedSession> ScanCursor(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        var path = CursorGlobalDbPath;
        if (!File.Exists(path)) return new List<ScannedSession>();
        var modified = Mtime(path);

        List<CursorConversation>? cached = null;
        lock (CursorGate)
        {
            if (modified == _cursorStamp) cached = _cursorCache;
        }
        if (cached is not null)
        {
            // Status is NEVER cached — only the parsed conversation is. A
            // finished turn becomes "your turn" purely by the clock moving
            // past the quiet gap, and the db stops changing the moment the
            // assistant stops writing, so a cached status would freeze at
            // "working" and the alarm would never fire.
            return cached.ConvertAll(c => CursorSession(c, now, lastWorking));
        }

        var parsed = new List<CursorConversation>();
        foreach (var (composerId, json) in CursorConversations.NewestBubblePerConversation(path))
        {
            var turn = SessionTurnState.Cursor(new[] { json });
            // No usable bubble timestamp means we cannot say WHEN this
            // conversation last moved. Falling back to the db mtime was the
            // bug behind a permanently spinning logo: Cursor rewrites that
            // file continuously while it is merely open.
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

    private static readonly object CursorGate = new();
    private static DateTimeOffset _cursorStamp = DateTimeOffset.MinValue;
    private static List<CursorConversation> _cursorCache = new();

    /// One parsed conversation. Deliberately holds no status: status is a
    /// function of the CURRENT clock and is recomputed on every scan.
    private readonly record struct CursorConversation(
        string Id, string Label, bool IsDone, string? TurnKey, DateTimeOffset Stamp);

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

    private static string CursorGlobalDbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cursor", "User", "globalStorage", "state.vscdb");


    /// Assistant-last plus a quiet gap means the turn finished; assistant-last
    /// while still streaming reads as working. A user bubble last means the
    /// agent is thinking.
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


    /// Antigravity keeps a readable transcript per conversation at
    /// `<root>/brain/<conversation-id>/.system_generated/logs/transcript_full.jsonl`
    /// — the desktop IDE and the `agy` CLI write the same shape into their
    /// own roots. Always `transcript_full`, never `transcript` (truncated).
    public static List<ScannedSession> ScanAntigravity(
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
            foreach (var conversationDir in SafeEnumerateDirectories(brain))
            {
                var conversation = Path.GetFileName(conversationDir);
                var path = Path.Combine(
                    conversationDir, ".system_generated", "logs", "transcript_full.jsonl");
                if (!File.Exists(path)) continue;
                var state = SessionState(
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

    /// One row per conversation in `conversation_summaries.db`, the plain
    /// SQLite index the CLI keeps beside `brain/`. The db is WAL-mode: a
    /// read-only open needs the -shm file, which only exists while agy
    /// holds the db open — with agy closed the plain open fails, so the
    /// immutable URI (which skips the WAL entirely) is the fallback. Safe:
    /// a cleanly closed db is fully checkpointed, and a stale miss only
    /// costs a nicer label.
    private static Dictionary<string, (string? Title, string? Workspace)> AntigravitySummaries(string root)
    {
        var dbPath = Path.Combine(root, "conversation_summaries.db");
        if (!File.Exists(dbPath)) return new();
        return AntigravitySummaryRows($"Data Source={dbPath};Mode=ReadOnly")
            ?? AntigravitySummaryRows(
                $"Data Source=file:{Uri.EscapeDataString(dbPath).Replace("%5C", "/").Replace("%3A", ":")}?immutable=1;Mode=ReadOnly")
            ?? new();
    }

    /// null means this open failed and the caller should try the other
    /// mode; an empty dictionary means the table really had nothing.
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
                // title is usually blank; preview carries the generated name.
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

    /// `workspace_uris` is a JSON array of file:// URIs; the first one is
    /// the session's directory. Empty for sessions started outside a
    /// project.
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
            // Malformed JSON only costs the cwd nicety.
        }
        return null;
    }

    /// The first user message, unwrapped from the `<USER_REQUEST>` envelope
    /// the CLI writes — the fallback when the summaries table has no row
    /// yet (it is written asynchronously).
    private static string? AntigravityTitle(string path)
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
            // Unreadable transcript — the id-prefix fallback covers it.
        }
        return null;
    }

    internal static string? AntigravityRequestText(string content)
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

    /// `history.jsonl` maps conversationId to its workspace.
    private static string? AntigravityWorkspace(string root, string conversation)
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
            // A torn history file only costs the cwd nicety.
        }
        return null;
    }

    // MARK: - Indexes

    public static Dictionary<string, string> ClaudeTranscriptIndex()
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in IslandPaths.ClaudeProjectRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var path in SafeEnumerateFiles(root, "*.jsonl"))
            {
                // Subagent transcripts (subagents/ dirs, agent-*.jsonl) are
                // machine fan-out, not user conversations, and never surface.
                // Matched on the path RELATIVE to the root: an absolute path
                // can carry an "agent-…" segment from the user's own folder
                // names (this repo lives in one).
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

    private sealed record DesktopSession(
        string CliSessionId, string Title, string Cwd, bool IsArchived, DateTimeOffset? LastActivityAt);

    private static IEnumerable<string> EnumerateDesktopSessionFiles()
    {
        var root = IslandPaths.ClaudeDesktopSessionsRoot;
        if (!Directory.Exists(root)) return Array.Empty<string>();
        return SafeEnumerateFiles(root, "local_*.json");
    }

    /// Recursively lists matching files, surviving a subdirectory that is
    /// inaccessible OR deleted mid-walk (Claude Code rotates project folders).
    /// A plain EnumerateFiles(AllDirectories) throws DURING iteration — outside
    /// any try around the call — which used to fault the whole scan and, with
    /// it, every turn alarm. Skips reparse points to avoid symlink loops.
    internal static List<string> SafeEnumerateFiles(string root, string pattern)
    {
        var result = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            using var walker = Directory.EnumerateFiles(root, pattern, options).GetEnumerator();
            while (true)
            {
                try
                {
                    if (!walker.MoveNext()) break;
                }
                catch
                {
                    // A directory vanished or turned unreadable mid-walk —
                    // stop cleanly and keep what we already gathered.
                    break;
                }
                result.Add(walker.Current);
            }
        }
        catch
        {
        }
        return result;
    }

    /// Immediate subdirectories only, tolerating a missing root and a
    /// directory that vanishes mid-walk (Grok and Gemini both rotate their
    /// per-project folders while the monitor is running).
    private static List<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return new List<string>();
            return Directory.EnumerateDirectories(root).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// Grok's per-cwd folder name is percent-encoded, the same trick Claude's
    /// projects folder uses. Decoding is best-effort: a name that is not
    /// valid percent-encoding — or that decodes to nothing — keeps its raw
    /// form rather than leaving the session with an empty cwd.
    private static string DecodePathSegment(string name)
    {
        if (name.Length == 0) return name;
        try
        {
            var decoded = Uri.UnescapeDataString(name);
            return decoded.Length == 0 ? name : decoded;
        }
        catch
        {
            return name;
        }
    }

    private static DesktopSession? ParseDesktopSessionFile(string path)
    {
        try
        {
            using var stream = OpenShared(path);
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

    private static Dictionary<string, DesktopSession> ClaudeDesktopIndex()
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

    private static Dictionary<string, string> CodexTitleIndex()
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = IslandPaths.CodexSessionIndexFile;
        if (!File.Exists(path)) return output;
        string[] lines;
        try
        {
            using var stream = OpenShared(path);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            lines = reader.ReadToEnd().Split('\n');
        }
        catch
        {
            return output;
        }
        foreach (var line in lines)
        {
            using var doc = Jsonl.TryParseLine(line);
            if (doc is null) continue;
            var id = Jsonl.GetString(doc.RootElement, "id");
            var title = Jsonl.GetString(doc.RootElement, "thread_name")?.Trim();
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(title)) output[id!] = title!;
        }
        return output;
    }

    // MARK: - State machine

    public static (ActivityState Status, string? TurnKey, DateTimeOffset Modified) SessionState(
        string? path,
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        DateTimeOffset? externalActivityDate,
        Func<IReadOnlyList<string>, SessionTurnStatus> turnState,
        bool quietMeansDone = false)
    {
        if (path is null)
            return (ActivityState.Idle, null, externalActivityDate ?? DateTimeOffset.MinValue);

        var (turn, fileModified) = ReadTurn(path, turnState);
        // Providers whose transcripts carry no explicit turn boundary
        // (Gemini's checkpoint stream, Cursor's workspace db) still have an
        // honest completion signal: the file was being written moments ago
        // and has now gone quiet. A CLI that stopped writing is either
        // finished or waiting on an approval — both mean "your turn". The
        // quiet threshold sits well above streaming gaps so a thinking
        // pause never fires it.
        if (quietMeansDone && !turn.IsDone && !turn.IsRunning
            && lastWorking.TryGetValue(path, out var lastActive))
        {
            var quietFor = (now - fileModified).TotalSeconds;
            if (quietFor > GuestQuietAfterSeconds
                && (now - lastActive) < NeedsYouCap)
            {
                turn = new SessionTurnStatus(
                    true,
                    $"quiet:{lastActive.ToUnixTimeSeconds()}",
                    turn.ActivityDate);
            }
        }
        var semanticModified = LatestDate(turn.ActivityDate, externalActivityDate);
        var effectiveModified = semanticModified ?? fileModified;
        // For a finished turn, external activity inside the bookkeeping grace
        // is Claude Desktop's own post-turn write — not the user returning —
        // so it must not suppress needsYou. Genuine "user came back" activity
        // lands minutes later, far past the grace.
        var externalReference = turn.IsDone
            ? turn.ActivityDate?.Add(DesktopBookkeepingGrace)
            : turn.ActivityDate;
        var externalIsNewer = IsLater(externalActivityDate, externalReference);
        var age = now - effectiveModified;

        if (age > AttentionWindow) return (ActivityState.Idle, turn.Key, effectiveModified);
        if (externalIsNewer)
        {
            if (age < StallAfter) return (ActivityState.Working, turn.Key, effectiveModified);
            if (lastWorking.TryGetValue(path, out var seen)
                && now - seen < StallCap
                && age < StallCap)
            {
                return (ActivityState.Stalled, turn.Key, effectiveModified);
            }
            return (ActivityState.Idle, turn.Key, effectiveModified);
        }
        if (turn.IsDone)
            return (age < NeedsYouCap ? ActivityState.NeedsYou : ActivityState.Idle, turn.Key, effectiveModified);
        if (age < ActiveWindow) return (ActivityState.Working, turn.Key, effectiveModified);
        if (age < StallAfter) return (ActivityState.Working, turn.Key, effectiveModified);
        if (lastWorking.TryGetValue(path, out var seenAgain)
            && now - seenAgain < StallCap
            && age < StallCap)
        {
            return (ActivityState.Stalled, turn.Key, effectiveModified);
        }
        return (ActivityState.Idle, turn.Key, effectiveModified);
    }

    // MARK: - Turn parse cache

    private static readonly Dictionary<string, (long Ticks, long Size, SessionTurnStatus Turn, DateTimeOffset Modified)>
        TurnCache = new(StringComparer.Ordinal);

    /// The monitoring scan runs on its own worker while the trigger picker's
    /// scan runs on another; both land here. An unsynchronized Dictionary
    /// written from two threads corrupts its bucket chain, and a corrupted
    /// chain makes the NEXT lookup spin forever — a hung scan thread, not an
    /// exception. The parse itself stays outside the lock so one slow tail
    /// read never blocks the other scan.
    private static readonly object TurnCacheGate = new();

    /// The expensive half of SessionState — the tail read plus the JSON turn
    /// parse — depends only on file CONTENT, so it is cached on a (mtime, size)
    /// fingerprint. Transcripts are append-only: a byte written bumps Length,
    /// so an unchanged fingerprint provably means the parsed turn still holds.
    ///
    /// This is the fix for the monitor pegging a CPU core on a machine with
    /// many sessions: the file-event path kicks a FULL scan on every transcript
    /// write, and without this every kick re-read and re-parsed the tail of
    /// EVERY session. Now an idle session is a two-field stat, and only the one
    /// transcript actually being written gets re-parsed. The time-based state
    /// (working / stalled / needsYou) is still computed fresh each scan from
    /// the cached turn, so nothing about detection changes — only redundant IO.
    private static (SessionTurnStatus Turn, DateTimeOffset Modified) ReadTurn(
        string path, Func<IReadOnlyList<string>, SessionTurnStatus> turnState)
    {
        try
        {
            var info = new FileInfo(path);
            var ticks = info.LastWriteTimeUtc.Ticks;
            var size = info.Length;
            lock (TurnCacheGate)
            {
                if (TurnCache.TryGetValue(path, out var cached)
                    && cached.Ticks == ticks && cached.Size == size)
                {
                    return (cached.Turn, cached.Modified);
                }
            }
            var modified = Mtime(path);
            var parsed = turnState(TailLines(path));
            lock (TurnCacheGate)
            {
                // Bound the cache against pathological growth (project-folder
                // rotation minting new transcript paths forever); the working set
                // is one entry per real session, rebuilt cheaply after a clear.
                if (TurnCache.Count > 5000) TurnCache.Clear();
                TurnCache[path] = (ticks, size, parsed, modified);
            }
            return (parsed, modified);
        }
        catch
        {
            // Stat/read raced a folder rotation — read directly, uncached.
            // A second failure means the file is unreadable right now, which
            // is "no turn yet", never a fault that takes the whole scan (and
            // with it every provider's state) down with it.
            try { return (turnState(TailLines(path)), Mtime(path)); }
            catch { return (default, Mtime(path)); }
        }
    }

    /// Test seam: drop the fingerprint cache so a suite that rewrites content
    /// behind a reused path never reads a stale parse.
    internal static void ClearTurnCache()
    {
        lock (TurnCacheGate) TurnCache.Clear();
    }

    // MARK: - Helpers

    public static string Fallback(string cwd, string sid)
    {
        var trimmed = cwd.TrimEnd('\\', '/');
        var basename = trimmed.Length == 0 ? "" : Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(basename))
            return sid.Length <= 8 ? sid : sid[..8];
        return basename;
    }

    /// The transcript itself records the true working directory on nearly
    /// every entry ("cwd") — authoritative, unlike the lossy encoded folder
    /// name, whose dashes un-munge real hyphenated paths into the wrong
    /// directory and then break `claude --resume` launched from it.
    internal static string CwdFromClaudeTranscript(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
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
                }
            }
        }
        catch
        {
        }
        return ProjectFromClaudeTranscript(path);
    }

    /// Display-only fallback when the transcript carries no cwd: reverse
    /// the encoded project directory name. The encoding is lossy (path
    /// separators and ':' both became '-'), so this is a best-effort label,
    /// same as on macOS.
    private static string ProjectFromClaudeTranscript(string path)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        if (parent.Length == 0) return "";
        // Windows transcripts encode e.g. C:\Users\me\proj as C--Users-me-proj.
        if (parent.Length > 3 && char.IsAsciiLetter(parent[0]) && parent[1] == '-' && parent[2] == '-')
        {
            return parent[0] + ":\\" + parent[3..].Replace('-', '\\');
        }
        // A UNC root (\\server\share\proj) loses both leading separators to
        // the same dash, so it arrives as --server-share-proj.
        if (parent.StartsWith("--", StringComparison.Ordinal))
        {
            return @"\\" + parent[2..].Replace('-', '\\');
        }
        // Anything else is a session recorded from a shell with its own path
        // shape (MSYS, WSL). Un-munge to backslashes anyway rather than mint a
        // POSIX-looking path no Windows surface can act on.
        return parent.Replace('-', '\\');
    }

    /// Every artifact this scanner reads belongs to a tool that is running
    /// right now — Claude Desktop's session store, Codex's session index,
    /// Grok's summary. The default File.ReadAll* share mode throws a sharing
    /// violation against an open writer, which would silently drop that
    /// session (or every Codex title) from the scan.
    private static FileStream OpenShared(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public static List<string> TailLines(string path, long bytes = 131_072, int keep = 200)
    {
        byte[] data;
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var size = stream.Length;
            if (size > bytes) stream.Seek(size - bytes, SeekOrigin.Begin);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            data = memory.ToArray();
        }
        catch
        {
            return new List<string>();
        }
        var text = Encoding.UTF8.GetString(data);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var start = Math.Max(0, lines.Length - keep);
        var output = new List<string>(Math.Min(keep, lines.Length));
        for (var i = start; i < lines.Length; i++)
        {
            output.Add(lines[i].TrimEnd('\r'));
        }
        return output;
    }

    public static DateTimeOffset Mtime(string path)
    {
        try
        {
            var utc = File.GetLastWriteTimeUtc(path);
            return utc.Year < 1700 ? DateTimeOffset.MinValue : new DateTimeOffset(utc);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static DateTimeOffset? LatestDate(DateTimeOffset? lhs, DateTimeOffset? rhs)
    {
        if (lhs is { } l && rhs is { } r) return l > r ? l : r;
        return lhs ?? rhs;
    }

    private static bool IsLater(DateTimeOffset? lhs, DateTimeOffset? rhs)
    {
        if (lhs is not { } l) return false;
        if (rhs is not { } r) return true;
        return (l - r).TotalSeconds > 0.5;
    }
}
