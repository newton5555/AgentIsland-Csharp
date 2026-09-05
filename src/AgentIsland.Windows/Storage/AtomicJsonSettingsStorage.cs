using System.IO;
using System.Text.Json;
using AgentIsland.Core.Storage;

namespace AgentIsland.Windows.Storage;

/// <summary>
/// Thread-safe, crash-resilient settings storage using atomic file replacement.
/// Provides memory caching and real-time change events.
/// </summary>
public sealed class AtomicJsonSettingsStorage : ISettingsStorage
{
    private readonly object _gate = new();
    private readonly string _filePath;
    private Dictionary<string, JsonElement>? _values;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public static AtomicJsonSettingsStorage Default { get; } = new(IslandPaths.SettingsFile);

    public event EventHandler<string>? SettingChanged;

    public AtomicJsonSettingsStorage(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    public T? Get<T>(string key)
    {
        lock (_gate)
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

    public bool Has(string key)
    {
        lock (_gate)
        {
            LoadIfNeeded();
            return _values!.ContainsKey(key);
        }
    }

    public void Set<T>(string key, T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            _values = LoadFromDisk();
            _values[key] = JsonSerializer.SerializeToElement(value);
            Save();
        }
        SettingChanged?.Invoke(this, key);
    }

    public void SetBatch(Action<Dictionary<string, JsonElement>> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var changedKeys = new List<string>();
        lock (_gate)
        {
            _values = LoadFromDisk();
            var beforeKeys = new HashSet<string>(_values.Keys, StringComparer.Ordinal);
            update(_values);
            Save();
            changedKeys.AddRange(_values.Keys.Where(k => !beforeKeys.Contains(k)));
        }
        foreach (var key in changedKeys)
        {
            SettingChanged?.Invoke(this, key);
        }
    }

    public void Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var removed = false;
        lock (_gate)
        {
            _values = LoadFromDisk();
            if (_values.Remove(key))
            {
                Save();
                removed = true;
            }
        }
        if (removed)
        {
            SettingChanged?.Invoke(this, key);
        }
    }

    public void MigrateLegacyPrefix()
    {
        lock (_gate)
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

    private void LoadIfNeeded()
    {
        _values ??= LoadFromDisk();
    }

    private Dictionary<string, JsonElement> LoadFromDisk()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var text = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text)
                    ?? new Dictionary<string, JsonElement>();
            }
        }
        catch
        {
        }
        return new Dictionary<string, JsonElement>();
    }

    private void Save()
    {
        var tmp = string.Empty;
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            tmp = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, JsonSerializer.Serialize(_values, SerializerOptions));
            var moved = false;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.Move(tmp, _filePath, overwrite: true);
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
                File.Move(tmp, _filePath, overwrite: true);
            }
        }
        catch
        {
            try { if (tmp.Length > 0 && File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
