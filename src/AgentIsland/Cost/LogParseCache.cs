using System.IO;
using System.Text.Json;
using AgentIsland.Core;

namespace AgentIsland.Cost;

/// Per-file parse memoization keyed by (mtime, size). Steady-state polls,
/// where almost no file changed, skip file I/O entirely; disappeared or
/// out-of-window files are pruned. Cache JSON lives under
/// %LOCALAPPDATA%\AgentIsland\cache.
public sealed class LogParseCache
{
    private sealed record EventDto(
        long Ts, string Model, long In, long Out, long Cc, long Cr);

    private sealed record Entry(long MtimeTicks, long Size, List<EventDto> Events);

    private readonly string _cachePath;
    private readonly TriggerTool _provider;
    private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;
    private bool _loaded;

    public LogParseCache(TriggerTool provider, string cacheFileName)
    {
        _provider = provider;
        _cachePath = Path.Combine(IslandPaths.CacheDir, cacheFileName);
    }

    public List<TokenEvent> Walk(
        IEnumerable<string> files,
        DateTimeOffset cutoff,
        Func<string, List<TokenEvent>> parseFile)
    {
        Load();
        var output = new List<TokenEvent>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            DateTimeOffset mtime;
            long size;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                mtime = new DateTimeOffset(info.LastWriteTimeUtc);
                size = info.Length;
            }
            catch
            {
                continue;
            }
            if (mtime < cutoff) continue;
            seen.Add(path);

            if (_entries.TryGetValue(path, out var entry)
                && entry.MtimeTicks == mtime.UtcTicks
                && entry.Size == size)
            {
                output.AddRange(entry.Events.Select(FromDto));
                continue;
            }

            var events = parseFile(path);
            _entries[path] = new Entry(mtime.UtcTicks, size, events.Select(ToDto).ToList());
            _dirty = true;
            output.AddRange(events);
        }

        // Prune entries for files gone or aged out of the window.
        var stale = _entries.Keys.Where(key => !seen.Contains(key)).ToList();
        if (stale.Count > 0)
        {
            foreach (var key in stale) _entries.Remove(key);
            _dirty = true;
        }
        SaveIfDirty();
        return output;
    }

    private TokenEvent FromDto(EventDto dto) => new(
        _provider,
        // Clamp: a corrupt cache file with an out-of-range Ts would otherwise
        // throw out of FromUnixTimeMilliseconds and take the whole cost scan
        // down on a cache hit.
        DateTimeOffset.FromUnixTimeMilliseconds(
            Math.Clamp(dto.Ts, -62_135_596_800_000, 253_402_300_799_999)),
        dto.Model,
        dto.In,
        dto.Out,
        dto.Cc,
        dto.Cr);

    private static EventDto ToDto(TokenEvent tokenEvent) => new(
        tokenEvent.Timestamp.ToUnixTimeMilliseconds(),
        tokenEvent.Model,
        tokenEvent.InputTokens,
        tokenEvent.OutputTokens,
        tokenEvent.CacheCreationTokens,
        tokenEvent.CacheReadTokens);

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(_cachePath))
            {
                var text = File.ReadAllText(_cachePath);
                _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(text)
                    ?? new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveIfDirty()
    {
        if (!_dirty) return;
        _dirty = false;
        try
        {
            Directory.CreateDirectory(IslandPaths.CacheDir);
            var tmp = _cachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch
        {
        }
    }
}
