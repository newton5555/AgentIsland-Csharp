using System.IO;
using AgentIsland.Core;
using AgentIsland.Providers.Sessions.DeepSeek;
using AgentIsland.Windows;
using AgentIsland.Windows.Storage;

namespace AgentIsland.Backend.Monitoring;

/// Host adapter for DeepSeek Harness' live activity stream. DSH writes one
/// compressed JSONL file per session; the provider parser understands the
/// event protocol while this adapter turns the result into the same
/// ScannedSession shape used by the other activity monitors.
///
/// Every DSH route is surfaced for activity. A session can contain several
/// route headers over its lifetime, so the parser's latest route is retained
/// as metadata while the latest event drives the state. Delegated worker
/// sessions are filtered; only a human-facing Harness session can drive the
/// whale. The official-route restriction belongs to the balance request, not
/// local activity detection.
public static class DeepSeekActivityReader
{
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(18);
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record CacheEntry(
        long Ticks,
        long Size,
        DeepSeekActivitySnapshot? Snapshot);

    public static List<ScannedSession> Scan(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        // DeepSeek has an explicit stream timestamp and a file mtime. The
        // monitor keeps no turn alarm for DSH, so lastWorking is intentionally
        // unused; retaining the common signature makes provider aggregation
        // uniform and leaves room for a future approval event.
        _ = lastWorking;
        var root = IslandPaths.DeepSeekSessionsRoot;
        if (!Directory.Exists(root)) return new List<ScannedSession>();

        var output = new List<ScannedSession>();
        foreach (var path in AgentIsland.Backend.Cost.DeepSeekLogReader.DiscoverSessionFiles(root))
        {
            var snapshot = Read(path);
            if (snapshot is null || !IsEligibleForActivity(snapshot)) continue;

            var sessionDirectory = Path.GetDirectoryName(path);
            var sessionId = Path.GetFileName(sessionDirectory ?? "");
            if (string.IsNullOrWhiteSpace(sessionId)) continue;

            var modified = SafeFileSystem.LastWriteTime(path);
            if (snapshot.LatestTimestamp is { } eventTime && eventTime > modified)
                modified = eventTime;
            if (modified == DateTimeOffset.MinValue) continue;

            var age = now - modified;
            var status = snapshot.LatestKind == DeepSeekActivityKind.Active
                && age <= ActiveWindow
                ? ActivityState.Working
                : ActivityState.Idle;
            var cwd = !string.IsNullOrWhiteSpace(snapshot.Cwd)
                ? snapshot.Cwd!
                : ProjectFromSessionPath(path);
            output.Add(new ScannedSession(
                TriggerTool.DeepSeek,
                sessionId,
                cwd,
                SessionScanner.Fallback(cwd, sessionId),
                modified,
                status,
                path,
                snapshot.TurnKey is { Length: > 0 } turnKey
                    ? $"deepseek:{turnKey}"
                    : $"deepseek:{sessionId}",
                SessionLaunchTarget.Cli));
        }

        output.Sort((left, right) => right.Modified.CompareTo(left.Modified));
        return output;
    }

    private static DeepSeekActivitySnapshot? Read(string path)
    {
        long ticks;
        long size;
        try
        {
            var info = new FileInfo(path);
            ticks = info.LastWriteTimeUtc.Ticks;
            size = info.Length;
        }
        catch
        {
            return null;
        }

        lock (CacheGate)
        {
            if (Cache.TryGetValue(path, out var cached)
                && cached.Ticks == ticks && cached.Size == size)
            {
                return cached.Snapshot;
            }
        }

        DeepSeekActivitySnapshot? snapshot;
        try
        {
            snapshot = DeepSeekActivityParser.ParseFile(path);
        }
        catch
        {
            snapshot = null;
        }

        lock (CacheGate)
        {
            if (Cache.Count > 5000) Cache.Clear();
            Cache[path] = new CacheEntry(ticks, size, snapshot);
        }
        return snapshot;
    }

    /// The DSH project folder is a display-only, lossy encoding such as
    /// `--F-Projects-LLRP-LLRPCSharp--`. Decode enough of it to give the
    /// island a useful project label; launching/resuming is deliberately not
    /// offered for DSH sessions yet.
    internal static string ProjectFromSessionPath(string path)
    {
        var project = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path) ?? "") ?? "");
        if (project.Length == 0) return "";
        var encoded = project.Trim('-');
        if (encoded.Length >= 2
            && char.IsAsciiLetter(encoded[0])
            && encoded[1] == '-')
        {
            return encoded[0] + ":\\" + encoded[2..].Replace('-', '\\');
        }
        if (encoded.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return encoded.Replace('-', '\\');
        }
        return encoded.Replace('-', '\\');
    }

    internal static void ClearCache()
    {
        lock (CacheGate) Cache.Clear();
    }

    /// All routes in a DSH session contribute to local activity. Keep this
    /// predicate separate from the official-balance rule so the two data
    /// sources cannot accidentally acquire the same filter again.
    internal static bool IsEligibleForActivity(DeepSeekActivitySnapshot snapshot) =>
        !snapshot.IsSubagent;
}
