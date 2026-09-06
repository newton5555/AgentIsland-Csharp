using System.Text.Json;

namespace AgentIsland.Core.Storage;

/// <summary>
/// In-memory implementation of ISettingsStorage for isolated tests and transient state.
/// </summary>
public sealed class MemorySettingsStorage : ISettingsStorage
{
    private readonly Dictionary<string, object> _data = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public event EventHandler<string>? SettingChanged;

    public T? Get<T>(string key)
    {
        lock (_lock)
        {
            if (_data.TryGetValue(key, out var val))
            {
                if (val is T typed) return typed;
                try
                {
                    return (T)Convert.ChangeType(val, typeof(T));
                }
                catch { }
            }
            return default;
        }
    }

    public bool Has(string key)
    {
        lock (_lock)
        {
            return _data.ContainsKey(key);
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_lock)
        {
            if (value is null) _data.Remove(key);
            else _data[key] = value;
        }
        SettingChanged?.Invoke(this, key);
    }

    public void SetBatch(Action<Dictionary<string, JsonElement>> update)
    {
        // No-op or serialize in memory if needed
    }

    public void Remove(string key)
    {
        lock (_lock)
        {
            _data.Remove(key);
        }
        SettingChanged?.Invoke(this, key);
    }
}
