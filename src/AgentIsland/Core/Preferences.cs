using System.IO;
using System.Text.Json;

namespace AgentIsland.Core;

/// UserDefaults equivalent: one JSON file of keyed values under
/// %APPDATA%\AgentIsland. Writes are atomic (temp + rename) so a crash
/// mid-save never truncates every preference at once.
///
/// Every write re-reads the file and layers only the caller's key on top.
/// The old write-my-whole-snapshot design silently destroyed data whenever
/// two copies of the app ran at once (portable exe + a second unzip, or the
/// updater's relaunch overlap): each instance's periodic saves kept
/// resurrecting its stale launch snapshot — which is how a freshly created
/// auto-resume rule could vanish minutes later.
public static class Preferences
{
    private static readonly object Gate = new();
    private static Dictionary<string, JsonElement>? _values;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public static T? Get<T>(string key)
    {
        lock (Gate)
        {
            LoadIfNeeded();
            if (_values!.TryGetValue(key, out var element))
            {
                try { return element.Deserialize<T>(); }
                catch { return default; }
            }
            return default;
        }
    }

    public static bool Has(string key)
    {
        lock (Gate)
        {
            LoadIfNeeded();
            return _values!.ContainsKey(key);
        }
    }

    public static void Set<T>(string key, T value)
    {
        lock (Gate)
        {
            _values = LoadFromDisk();
            _values[key] = JsonSerializer.SerializeToElement(value);
            Save();
        }
    }

    public static void SetBatch(Action<Dictionary<string, JsonElement>> update)
    {
        lock (Gate)
        {
            _values = LoadFromDisk();
            update(_values);
            Save();
        }
    }

    public static void Remove(string key)
    {
        lock (Gate)
        {
            _values = LoadFromDisk();
            if (_values.Remove(key)) Save();
        }
    }

    /// One-time sweep of the MacIsland.* era: settings written before 1.7
    /// carried the prefix inherited from the macOS source. Copy-if-absent
    /// then delete — an AgentIsland.* value that already exists wins, so
    /// running an old build after the sweep can't resurrect stale state
    /// over a newer choice. Runs before any store singleton reads a key.
    public static void MigrateLegacyPrefix()
    {
        lock (Gate)
        {
            _values = LoadFromDisk();
            var legacy = _values.Keys
                .Where(k => k.StartsWith("MacIsland.", StringComparison.Ordinal))
                .ToList();
            if (legacy.Count == 0) return;
            foreach (var oldKey in legacy)
            {
                var newKey = "AgentIsland." + oldKey["MacIsland.".Length..];
                if (!_values.ContainsKey(newKey)) _values[newKey] = _values[oldKey];
                _values.Remove(oldKey);
            }
            Save();
        }
    }

    private static void LoadIfNeeded()
    {
        _values ??= LoadFromDisk();
    }

    private static Dictionary<string, JsonElement> LoadFromDisk()
    {
        try
        {
            var path = IslandPaths.SettingsFile;
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text)
                    ?? new Dictionary<string, JsonElement>();
            }
        }
        catch
        {
        }
        return new Dictionary<string, JsonElement>();
    }

    private static void Save()
    {
        // Per-write temp name, not a fixed ".tmp". Windows denies a second
        // opener while the first holds the handle, so with two copies of the
        // app running — the very case the re-read-and-layer design above
        // exists for — one instance's write would fail and be swallowed here,
        // silently losing a preference change.
        var tmp = string.Empty;
        try
        {
            Directory.CreateDirectory(IslandPaths.AppSupportDir);
            var path = IslandPaths.SettingsFile;
            tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, JsonSerializer.Serialize(_values, SerializerOptions));
            var moved = false;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    moved = true;
                    break;
                }
                catch (IOException)
                {
                    Thread.Sleep(20);
                }
            }
            if (!moved)
            {
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch
        {
            // A failed save costs one preference write, not the app — but the
            // temp copy must not be left behind, since nothing else sweeps it.
            try { if (tmp.Length > 0 && File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
