using System.Buffers;
using System.IO;
using System.Text;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Providers.Sessions;
using AgentIsland.Windows;
using AgentIsland.Windows.Storage;

namespace AgentIsland.Backend.Monitoring.Sensors;

public sealed class CodexSessionSensor : ISessionSensor
{
    public enum CodexRolloutKind
    {
        Interactive,
        Subagent,
        Automation,
    }

    private const int MaxCodexFirstLineBytes = 2_000_000;
    private const int CodexInitialBufferSize = 65_536;

    private static readonly Dictionary<string, (long Ticks, long Size, (string Sid, string Cwd, CodexRolloutKind Kind)? Meta)>
        CodexMetaCache = new(StringComparer.Ordinal);
    private static readonly object CodexMetaCacheGate = new();

    public ValueTask<IReadOnlyList<ScannedSession>> ScanSessionsAsync(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        CancellationToken ct = default)
    {
        return ValueTask.FromResult<IReadOnlyList<ScannedSession>>(Scan(now, lastWorking, limit: 120, dedupeProjects: false));
    }

    public static List<ScannedSession> Scan(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        int limit = 30,
        bool dedupeProjects = true)
    {
        var root = IslandPaths.CodexSessionsRoot;
        if (!Directory.Exists(root)) return new List<ScannedSession>();

        var files = SafeFileSystem.EnumerateFiles(root, "*.jsonl");
        var mtimes = new Dictionary<string, DateTimeOffset>(files.Count, StringComparer.Ordinal);
        foreach (var f in files) mtimes[f] = SafeFileSystem.LastWriteTime(f);
        files.Sort((a, b) => mtimes[b].CompareTo(mtimes[a]));

        var titles = CodexTitleIndex();
        var output = new List<ScannedSession>();
        var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            if (CodexMeta(path) is not { } meta || string.IsNullOrEmpty(meta.Sid)) continue;
            var (sid, cwd, kind) = meta;
            if (kind is CodexRolloutKind.Automation or CodexRolloutKind.Subagent) continue;
            var projectKey = string.IsNullOrEmpty(cwd) ? sid : cwd;
            if (dedupeProjects && !seenProjects.Add(projectKey)) continue;
            var state = SessionScanner.SessionState(path, now, lastWorking, null, SessionTurnState.Codex);
            output.Add(new ScannedSession(
                TriggerTool.Codex,
                sid,
                cwd,
                titles.TryGetValue(sid, out var title) ? title : SessionScanner.Fallback(cwd, sid),
                state.Modified,
                state.Status,
                path,
                state.TurnKey,
                SessionLaunchTarget.Cli));
            if (output.Count >= limit) break;
        }
        return output;
    }

    public static (string Sid, string Cwd, CodexRolloutKind Kind)? CodexMeta(string path)
    {
        long ticks = 0;
        long size = 0;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            ticks = info.LastWriteTimeUtc.Ticks;
            size = info.Length;
            lock (CodexMetaCacheGate)
            {
                if (CodexMetaCache.TryGetValue(path, out var cached)
                    && cached.Ticks == ticks && cached.Size == size)
                {
                    return cached.Meta;
                }
            }
        }
        catch
        {
            return null;
        }

        var meta = ReadCodexMetaDirect(path);
        lock (CodexMetaCacheGate)
        {
            if (CodexMetaCache.Count > 5000) CodexMetaCache.Clear();
            CodexMetaCache[path] = (ticks, size, meta);
        }
        return meta;
    }

    public static void ClearCache()
    {
        lock (CodexMetaCacheGate)
        {
            CodexMetaCache.Clear();
        }
    }

    private static (string Sid, string Cwd, CodexRolloutKind Kind)? ReadCodexMetaDirect(string path)
    {
        var rented = ArrayPool<byte>.Shared.Rent(CodexInitialBufferSize);
        try
        {
            var totalRead = 0;
            var lineLength = -1;
            using (var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                while (true)
                {
                    if (totalRead >= MaxCodexFirstLineBytes)
                    {
                        break;
                    }

                    if (totalRead >= rented.Length)
                    {
                        var nextCapacity = Math.Min(rented.Length * 2, MaxCodexFirstLineBytes);
                        if (nextCapacity <= rented.Length)
                        {
                            nextCapacity = MaxCodexFirstLineBytes;
                        }
                        var newRented = ArrayPool<byte>.Shared.Rent(nextCapacity);
                        Buffer.BlockCopy(rented, 0, newRented, 0, totalRead);
                        ArrayPool<byte>.Shared.Return(rented);
                        rented = newRented;
                    }

                    var toRead = Math.Min(rented.Length - totalRead, MaxCodexFirstLineBytes - totalRead);
                    if (toRead <= 0)
                    {
                        break;
                    }

                    var read = stream.Read(rented, totalRead, toRead);
                    if (read <= 0)
                    {
                        break;
                    }

                    var chunkSpan = rented.AsSpan(totalRead, read);
                    var newlineRel = chunkSpan.IndexOf((byte)'\n');
                    if (newlineRel >= 0)
                    {
                        lineLength = totalRead + newlineRel;
                        break;
                    }

                    totalRead += read;
                }
            }

            var length = lineLength >= 0 ? lineLength : totalRead;
            return ParseCodexMeta(Encoding.UTF8.GetString(rented.AsSpan(0, length)));
        }
        catch
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static (string Sid, string Cwd, CodexRolloutKind Kind)? ParseCodexMeta(string firstLine)
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

        var originator = Jsonl.GetString(payload, "originator") ?? "";
        if (originator.Length > 0
            && (!originator.StartsWith("codex", StringComparison.OrdinalIgnoreCase)
                || string.Equals(originator, "codex_exec", StringComparison.OrdinalIgnoreCase)))
        {
            kind = CodexRolloutKind.Automation;
        }

        return (Jsonl.GetString(payload, "id") ?? "", Jsonl.GetString(payload, "cwd") ?? "", kind);
    }

    public static Dictionary<string, string> CodexTitleIndex()
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = IslandPaths.CodexSessionIndexFile;
        if (!File.Exists(path)) return output;
        string[] lines;
        try
        {
            using var stream = SessionScanner.OpenShared(path);
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
}
