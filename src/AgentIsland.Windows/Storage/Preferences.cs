using System.Text.Json;
using AgentIsland.Core.Storage;
using AgentIsland.Windows.Storage;

namespace AgentIsland.Windows;

/// <summary>
/// Backward-compatible static facade for settings access, delegating to AtomicJsonSettingsStorage.
/// Preserves zero-breakage for all existing UI stores, views, and unit tests.
public static class Preferences
{
    private static ISettingsStorage? _overrideStorage;
    public static ISettingsStorage Storage
    {
        get => _overrideStorage ?? AtomicJsonSettingsStorage.Default;
        set => _overrideStorage = value;
    }

    public static T? Get<T>(string key) => Storage.Get<T>(key);

    public static bool Has(string key) => Storage.Has(key);

    public static void Set<T>(string key, T value) => Storage.Set(key, value);

    public static void SetBatch(Action<Dictionary<string, JsonElement>> update) => Storage.SetBatch(update);

    public static void Remove(string key) => Storage.Remove(key);

    public static void MigrateLegacyPrefix()
    {
        if (Storage is AtomicJsonSettingsStorage atomic)
        {
            atomic.MigrateLegacyPrefix();
        }
    }
}
