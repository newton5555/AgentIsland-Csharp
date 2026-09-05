using System.IO;
using System.Text.Json;
using AgentIsland.Core;

namespace AgentIsland.Core.Cost;

/// Per-file parse memoization keyed by (mtime, size). Steady-state polls,
/// where almost no file changed, skip file I/O entirely; disappeared or
/// out-of-window files are pruned. The cache file location is supplied by the
/// host so this type does not know about Windows folders or any other
/// platform's directory conventions.
public sealed class LogParseCache
{
    private readonly record struct EventDto(
        long Ts, string Model, long In, long Out, long Cc, long Cr);

    private sealed record Entry(long MtimeTicks, long Size, List<EventDto> Events);

    private readonly string _cachePath;
    private readonly TriggerTool _provider;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;
    private bool _loaded;
    private long _memoryGeneration;
    private long _changeVersion;
    private Task? _loadTask;

    public LogParseCache(TriggerTool provider, string cacheFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFilePath);
        _provider = provider;
        _cachePath = cacheFilePath;
    }

    /// Drops the in-process parsed-event graph but keeps the disk cache.
    ///
    /// The next enabled scan can warm the cache again from disk without
    /// making a provider's disabled state retain tens of megabytes of event
    /// DTOs for the lifetime of the app. This deliberately does not delete
    /// the JSON file: disabling an agent is a memory-lifetime decision, not a
    /// request to discard the user's local cost history.
    public void ClearMemory()
    {
        lock (_gate)
        {
            // Replace the dictionary as well as clearing it so a provider
            // that once held a large file set does not retain its bucket/
            // entry arrays. The lock also keeps a report's on-demand slice
            // from racing a settings toggle that releases this cache.
            _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            _dirty = false;
            _loaded = false;
            _memoryGeneration++;
            _changeVersion++;
        }
    }

    public List<TokenEvent> Walk(
        IEnumerable<string> files,
        DateTimeOffset cutoff,
        Func<string, List<TokenEvent>> parseFile,
        CancellationToken cancellationToken = default)
    {
        EnsureLoaded();

        var fileList = files.ToArray();
        Dictionary<string, Entry> snapshot;
        long generation;
        lock (_gate)
        {
            snapshot = new Dictionary<string, Entry>(_entries, StringComparer.OrdinalIgnoreCase);
            generation = _memoryGeneration;
        }

        var output = new List<TokenEvent>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = new List<(string Path, Entry Entry)>();
        foreach (var path in fileList)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            if (snapshot.TryGetValue(path, out var entry)
                && entry.MtimeTicks == mtime.UtcTicks
                && entry.Size == size)
            {
                output.AddRange(entry.Events.Select(FromDto));
                continue;
            }

            var events = parseFile(path);
            cancellationToken.ThrowIfCancellationRequested();
            // Never cache 0 events for a non-trivial file: if it failed or was
            // partially read while locked, subsequent scans should retry it.
            if (events.Count > 0 || size <= 1024)
            {
                var next = new Entry(mtime.UtcTicks, size, events.Select(ToDto).ToList());
                changed.Add((path, next));
                output.AddRange(events);
            }
            else
            {
                output.AddRange(events);
            }
        }

        // Commit only after all file I/O and parsing has left the hot path.
        // ClearMemory increments the generation, so a scan that was disabled
        // midway cannot repopulate the cache after the user turned it off.
        lock (_gate)
        {
            if (generation == _memoryGeneration)
            {
                foreach (var (path, entry) in changed)
                {
                    _entries[path] = entry;
                    _dirty = true;
                }

                // Prune entries for files gone or aged out of the window.
                var stale = _entries.Keys.Where(key => !seen.Contains(key)).ToList();
                foreach (var key in stale)
                {
                    _entries.Remove(key);
                    _dirty = true;
                }
                if (changed.Count > 0 || stale.Count > 0) _changeVersion++;
            }
        }
        SaveIfDirty(generation);
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

    private void EnsureLoaded()
    {
        while (true)
        {
            Task loadTask;
            lock (_gate)
            {
                if (_loaded) return;
                if (_loadTask is null)
                {
                    var generation = _memoryGeneration;
                    var completion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _loadTask = completion.Task;
                    _ = Task.Run(() => LoadFromDisk(completion, generation));
                }
                loadTask = _loadTask;
            }
            // All calls are made by the background query path. Waiting here is
            // preferable to exposing an empty snapshot to a second Walk, and
            // ClearMemory never waits on this task.
            loadTask.GetAwaiter().GetResult();
        }
    }

    private void LoadFromDisk(TaskCompletionSource completion, long loadGeneration)
    {
        var loaded = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var dirty = false;
        try
        {
            if (File.Exists(_cachePath))
            {
                using var stream = new FileStream(
                    _cachePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024, useAsync: false);
                var deserialized = JsonSerializer.Deserialize<Dictionary<string, Entry>>(stream);
                loaded = deserialized is null
                    ? new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, Entry>(deserialized, StringComparer.OrdinalIgnoreCase);

                // Prune legacy poisoned entries where a sharing violation or
                // crash cached a non-empty session with zero events. Also
                // reject malformed DTOs instead of turning a corrupt cache
                // hit into a TokenEvent with a null model.
                var poisoned = loaded
                    .Where(kv => kv.Value is null
                        || kv.Value.Events is null
                        || (kv.Value.Events.Count == 0 && kv.Value.Size > 1024)
                        || kv.Value.Events.Any(item => string.IsNullOrWhiteSpace(item.Model)))
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var key in poisoned) loaded.Remove(key);
                dirty = poisoned.Count > 0;
            }
        }
        catch
        {
            loaded = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
        lock (_gate)
        {
            // Another thread may have cleared memory while disk I/O ran. In
            // that case leave the freshly cleared state unloaded so the next
            // enabled scan performs the intended warm-up.
            if (loadGeneration == _memoryGeneration && !_loaded)
            {
                _entries = loaded;
                _dirty |= dirty;
                _loaded = true;
            }
            _loadTask = null;
        }
        completion.SetResult();
    }

    private void SaveIfDirty(long generation)
    {
        _saveGate.Wait();
        lock (_gate)
        {
            if (!_dirty || generation != _memoryGeneration)
            {
                _saveGate.Release();
                return;
            }
        }

        var tmp = string.Empty;
        try
        {
            Dictionary<string, Entry> snapshot;
            long changeVersion;
            lock (_gate)
            {
                if (!_dirty || generation != _memoryGeneration)
                {
                    return;
                }
                snapshot = new Dictionary<string, Entry>(_entries, StringComparer.OrdinalIgnoreCase);
                changeVersion = _changeVersion;
                _dirty = false;
            }

            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            tmp = _cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(
                tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: false))
            {
                JsonSerializer.Serialize(stream, snapshot);
                stream.Flush(flushToDisk: false);
            }
            lock (_gate)
            {
                if (generation != _memoryGeneration || changeVersion != _changeVersion)
                {
                    try { File.Delete(tmp); } catch { }
                    return;
                }
            }
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch
        {
            // Keep the dirty bit for the next poll if serialization or the
            // atomic replace failed. A later save always takes the latest
            // snapshot while holding _saveGate.
            lock (_gate)
            {
                if (generation == _memoryGeneration) _dirty = true;
            }
            if (tmp.Length > 0)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
