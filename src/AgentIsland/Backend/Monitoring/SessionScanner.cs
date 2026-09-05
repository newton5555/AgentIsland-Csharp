using System.Buffers;
using System.IO;
using System.Text;
using AgentIsland.Core;
using AgentIsland.Providers.Sessions;
using AgentIsland.Backend.Monitoring.Sensors;

namespace AgentIsland.Backend.Monitoring;

/// Discovers Claude Code / Claude Desktop / Codex / Grok / DeepSeek Harness
/// from the local artifacts those tools already write, and classifies each one
/// through a conservative state machine. Direct port of the macOS
/// SessionScanner; the thresholds are the tuned values from the shipping app.
/// Cursor's live signal comes from the workspace state database and is kept on
/// the same conservative recency path as the other guest providers.
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
    /// entry per project, plus every Grok and Gemini session. DeepSeek
    /// Harness is monitoring-only until a safe resume target exists.
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
    /// every recent Codex rollout with no project dedupe, plus every Grok,
    /// Gemini, and official DeepSeek Harness session. Subagent / child
    /// threads never participate — machine fan-out finishes dozens of threads
    /// per prompt and a human is never "up"
    /// in any of them, so they are skipped outright rather than gated behind a
    /// toggle (owner call, 2026-08-08).
    ///
    /// The provider set is optional to preserve the picker/test entry point's
    /// historical behavior. The live monitor passes its enabled set so a
    /// disabled provider is not even enumerated or opened.
    public static List<ScannedSession> MonitoringScan(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        IReadOnlySet<TriggerTool>? providers = null)
    {
        bool IsEnabled(TriggerTool provider) => providers is null || providers.Contains(provider);

        var output = new List<ScannedSession>();
        if (IsEnabled(TriggerTool.Claude))
            output.AddRange(ScanClaudeTranscripts(now, lastWorking, excludeArchived: false));
        if (IsEnabled(TriggerTool.Codex))
            output.AddRange(ScanCodex(now, lastWorking, limit: MonitoringCodexLimit, dedupeProjects: false));
        if (IsEnabled(TriggerTool.Grok))
            output.AddRange(ScanGrok(now, lastWorking));
        if (IsEnabled(TriggerTool.Antigravity))
            output.AddRange(ScanAntigravity(now, lastWorking));
        if (IsEnabled(TriggerTool.Cursor))
            output.AddRange(ScanCursor(now, lastWorking));
        if (IsEnabled(TriggerTool.DeepSeek))
            output.AddRange(ScanDeepSeek(now, lastWorking));
        output.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        return output;
    }

    // MARK: - Sensor Delegations

    public static List<ScannedSession> ScanDeepSeek(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking) =>
        DeepSeekSessionSensor.Scan(now, lastWorking);

    private static List<ScannedSession> ScanClaudeFromDesktopStore(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking) =>
        ClaudeSessionSensor.ScanClaudeFromDesktopStore(now, lastWorking);

    public static List<ScannedSession> ScanClaudeTranscripts(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        bool excludeArchived = false) =>
        ClaudeSessionSensor.ScanClaudeTranscripts(now, lastWorking, excludeArchived);

    internal static bool IsClaudeSubagentTranscript(string path) =>
        ClaudeSessionSensor.IsClaudeSubagentTranscript(path);

    public static List<ScannedSession> ScanCodex(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        int limit = 30,
        bool dedupeProjects = true) =>
        CodexSessionSensor.Scan(now, lastWorking, limit, dedupeProjects);

    internal enum CodexRolloutKind
    {
        Interactive = CodexSessionSensor.CodexRolloutKind.Interactive,
        Subagent = CodexSessionSensor.CodexRolloutKind.Subagent,
        Automation = CodexSessionSensor.CodexRolloutKind.Automation,
    }

    internal static (string Sid, string Cwd, CodexRolloutKind Kind)? CodexMeta(string path)
    {
        var meta = CodexSessionSensor.CodexMeta(path);
        if (meta is null) return null;
        return (meta.Value.Sid, meta.Value.Cwd, (CodexRolloutKind)(int)meta.Value.Kind);
    }

    internal static (string Sid, string Cwd, CodexRolloutKind Kind)? ParseCodexMeta(string firstLine)
    {
        var meta = CodexSessionSensor.ParseCodexMeta(firstLine);
        if (meta is null) return null;
        return (meta.Value.Sid, meta.Value.Cwd, (CodexRolloutKind)(int)meta.Value.Kind);
    }

    public static List<ScannedSession> ScanGrok(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking) =>
        GrokSessionSensor.Scan(now, lastWorking);

    public static List<ScannedSession> ScanCursor(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking) =>
        CursorSessionSensor.Scan(now, lastWorking);

    public static List<ScannedSession> ScanAntigravity(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking) =>
        AntigravitySessionSensor.Scan(now, lastWorking);

    internal static string? AntigravityRequestText(string content) =>
        AntigravitySessionSensor.AntigravityRequestText(content);

    public static Dictionary<string, string> ClaudeTranscriptIndex() =>
        ClaudeSessionSensor.ClaudeTranscriptIndex();

    public static Dictionary<string, string> CodexTitleIndex() =>
        CodexSessionSensor.CodexTitleIndex();

    internal static string DecodePathSegment(string name)
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
            var modified = SafeFileSystem.LastWriteTime(path);
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
            try { return (turnState(TailLines(path)), SafeFileSystem.LastWriteTime(path)); }
            catch { return (default, SafeFileSystem.LastWriteTime(path)); }
        }
    }

    /// Test seam: drop the fingerprint cache so a suite that rewrites content
    /// behind a reused path never reads a stale parse.
    internal static void ClearTurnCache()
    {
        lock (TurnCacheGate) TurnCache.Clear();
        CodexSessionSensor.ClearCache();
    }

    /// Drop only one provider's entries when a slot is disabled. The activity
    /// cache is shared for throughput, but clearing the whole dictionary would
    /// make an enabled sibling pay the parse cost again on its next tick.
    internal static void ClearTurnCache(TriggerTool provider)
    {
        if (provider == TriggerTool.Codex)
        {
            CodexSessionSensor.ClearCache();
        }

        IEnumerable<string> roots = provider switch
        {
            TriggerTool.Claude => IslandPaths.ClaudeProjectRoots,
            TriggerTool.Codex => new[] { IslandPaths.CodexSessionsRoot },
            TriggerTool.Grok => new[] { GrokSessionsRoot },
            TriggerTool.Antigravity => AntigravityRootNames.Select(name =>
                Path.Combine(IslandPaths.Home, ".gemini", name)),
            _ => Array.Empty<string>(),
        };

        var rootsList = roots.ToList();
        if (rootsList.Count == 0) return;
        lock (TurnCacheGate)
        {
            foreach (var path in TurnCache.Keys
                         .Where(path => rootsList.Any(root => IsUnderRoot(path, root)))
                         .ToList())
            {
                TurnCache.Remove(path);
            }
        }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(
                    fullRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// Cursor keeps a separate parsed-conversation cache because its live
    /// signal comes from state.vscdb rather than a transcript turn parser.
    /// Reset the stamp as well as the list so the next enable performs a
    /// fresh read even when Cursor has not changed the database meanwhile.
    internal static void ClearCursorCache()
    {
        CursorSessionSensor.ClearCache();
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
    internal static FileStream OpenShared(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public static List<string> TailLines(string path, long bytes = 131_072, int keep = 200)
    {
        var output = new List<string>(Math.Min(keep, 64));
        byte[]? rented = null;
        try
        {
            int totalRead = 0;
            using (var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                var size = stream.Length;
                if (size <= 0) return output;

                var toRead = (int)Math.Min(bytes, size);
                if (size > bytes) stream.Seek(size - toRead, SeekOrigin.Begin);

                rented = ArrayPool<byte>.Shared.Rent(toRead);
                while (totalRead < toRead)
                {
                    var read = stream.Read(rented, totalRead, toRead - totalRead);
                    if (read <= 0) break;
                    totalRead += read;
                }
            }

            if (totalRead <= 0) return output;

            var span = rented.AsSpan(0, totalRead);

            // Scan backwards to count up to `keep` newline bytes without allocating strings
            var newlineCount = 0;
            var startIndex = 0;
            for (var i = span.Length - 1; i >= 0; i--)
            {
                if (span[i] == (byte)'\n')
                {
                    newlineCount++;
                    if (newlineCount > keep)
                    {
                        startIndex = i + 1;
                        break;
                    }
                }
            }

            // Only decode the small slice containing the trailing lines (avoids LOH allocations)
            var tailSpan = span.Slice(startIndex);
            var text = Encoding.UTF8.GetString(tailSpan);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var start = Math.Max(0, lines.Length - keep);
            for (var i = start; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length > 0)
                {
                    output.Add(line);
                }
            }
            return output;
        }
        catch
        {
            return output;
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
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
