using System.Text.Json;

namespace AgentIsland.Core.Storage;

/// <summary>
/// Thread-safe storage interface for application preferences and configuration.
/// Supports atomic writes, batched updates, and change notifications.
/// </summary>
public interface ISettingsStorage
{
    T? Get<T>(string key);
    bool Has(string key);
    void Set<T>(string key, T value);
    void SetBatch(Action<Dictionary<string, JsonElement>> update);
    void Remove(string key);
    event EventHandler<string>? SettingChanged;
}
