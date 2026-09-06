using AgentIsland.Backend.Settings;
using AgentIsland.Core.Options;
using AgentIsland.Core.Storage;
using AgentIsland.Windows.Storage;
using Xunit;

namespace AgentIsland.Tests;

public class OptionsTests
{
    [Fact]
    public void TestAll() => RunAll();

    internal static void RunAll()
    {
        TestDefaultOptions();
        TestFreshInstallDefaultStores();
        TestUpdateAndPersistDisplayOptions();
        TestUpdateAndPersistPollingOptions();
        TestUpdateAndPersistAlertOptions();
        TestUpdateAndPersistProviderVisibility();
        TestOptionsMonitorOnChangeFired();
        TestStorageExternalChangeSyncsToOptions();
    }

    private static void TestFreshInstallDefaultStores()
    {
        var prevDataDir = Environment.GetEnvironmentVariable("AGENTISLAND_DATA_DIR");
        var testDir = Path.Combine(Path.GetTempPath(), "AgentIsland_TestFreshInstall_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        try
        {
            Environment.SetEnvironmentVariable("AGENTISLAND_DATA_DIR", testDir);

            var quotaStore = new QuotaDisplayModeStore();
            Assert.True(quotaStore.ShowsRemaining);

            var screenPref = new AgentIsland.UI.ScreenPref();
            Assert.False(screenPref.ShowCostPage);

            var lowPowerStore = new LowPowerModeStore();
            Assert.Equal(VisualMode.FollowModel, lowPowerStore.Mode);

            var alwaysShowStore = new AgentIsland.UI.AlwaysShowUsageStore();
            Assert.True(alwaysShowStore.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTISLAND_DATA_DIR", prevDataDir);
            try { Directory.Delete(testDir, recursive: true); } catch { }
        }
    }

    private static void TestDefaultOptions()
    {
        var tempStorage = new InMemorySettingsStorage();
        using var manager = new SettingsManager(tempStorage);

        Assert.Equal(IslandSpacingMode.NotchStyle, manager.Display.SpacingMode);
        Assert.Equal(1.0, manager.Display.SpacingScale);
        Assert.True(manager.Display.AlwaysShowUsage);
        Assert.Equal("teal", manager.Display.GlowColor);

        Assert.Equal(300, manager.Polling.RefreshIntervalSeconds);
        Assert.True(manager.Polling.AutoCheckUpdates);

        Assert.False(manager.Alerts.AlertsEnabled);
        Assert.Equal(80, manager.Alerts.WarningPercent);
        Assert.Equal(95, manager.Alerts.CriticalPercent);

        Assert.True(manager.ProviderVisibility.ClaudeVisible);
        Assert.True(manager.ProviderVisibility.CodexVisible);
    }

    private static void TestUpdateAndPersistDisplayOptions()
    {
        var tempStorage = new InMemorySettingsStorage();
        using var manager = new SettingsManager(tempStorage);

        bool eventFired = false;
        manager.DisplayChanged += (_, opt) =>
        {
            if (opt.SpacingMode == IslandSpacingMode.Compact && opt.AlwaysShowUsage && opt.GlowColor == "amber")
            {
                eventFired = true;
            }
        };

        manager.UpdateDisplay(opt =>
        {
            opt.SpacingMode = IslandSpacingMode.Compact;
            opt.AlwaysShowUsage = true;
            opt.GlowColor = "amber";
            opt.SpacingScale = 1.25;
            opt.ShowCostPanelPage = true;
        });

        Assert.True(eventFired);
        Assert.Equal(IslandSpacingMode.Compact, manager.Display.SpacingMode);
        Assert.True(manager.Display.AlwaysShowUsage);
        Assert.Equal("amber", manager.Display.GlowColor);
        Assert.Equal(1.25, manager.Display.SpacingScale);
        Assert.True(manager.Display.ShowCostPanelPage);

        // Verify underlying storage values
        Assert.Equal("Compact", tempStorage.Get<string>("AgentIsland.spacingMode"));
        Assert.Equal(1.25, tempStorage.Get<double>("AgentIsland.spacingScale"));
        Assert.True(tempStorage.Get<bool>("AgentIsland.alwaysShowUsage"));
        Assert.Equal("amber", tempStorage.Get<string>("AgentIsland.glowColor"));
        Assert.True(tempStorage.Get<bool>("AgentIsland.showCostPanelPage"));
    }

    private static void TestUpdateAndPersistPollingOptions()
    {
        var tempStorage = new InMemorySettingsStorage();
        using var manager = new SettingsManager(tempStorage);

        manager.UpdatePolling(opt =>
        {
            opt.RefreshIntervalSeconds = 120;
            opt.AutoCheckUpdates = false;
        });

        Assert.Equal(120, manager.Polling.RefreshIntervalSeconds);
        Assert.False(manager.Polling.AutoCheckUpdates);
        Assert.Equal(120, tempStorage.Get<int>("AgentIsland.refreshInterval"));
        Assert.Equal(120, tempStorage.Get<int>("AgentIsland.pollInterval"));
        Assert.False(tempStorage.Get<bool>("AgentIsland.autoCheckUpdates"));

        // Test clamping (min 60, max 3600)
        manager.UpdatePolling(opt => opt.RefreshIntervalSeconds = 10);
        Assert.Equal(60, manager.Polling.RefreshIntervalSeconds);

        manager.UpdatePolling(opt => opt.RefreshIntervalSeconds = 10000);
        Assert.Equal(3600, manager.Polling.RefreshIntervalSeconds);
    }

    private static void TestUpdateAndPersistAlertOptions()
    {
        var tempStorage = new InMemorySettingsStorage();
        using var manager = new SettingsManager(tempStorage);

        manager.UpdateAlerts(opt =>
        {
            opt.AlertsEnabled = true;
            opt.WarningPercent = 75;
            opt.CriticalPercent = 90;
        });

        Assert.True(manager.Alerts.AlertsEnabled);
        Assert.Equal(75, manager.Alerts.WarningPercent);
        Assert.Equal(90, manager.Alerts.CriticalPercent);

        // Test clamping
        manager.UpdateAlerts(opt =>
        {
            opt.WarningPercent = 10;
            opt.CriticalPercent = 150;
        });

        Assert.Equal(50, manager.Alerts.WarningPercent);
        Assert.Equal(99, manager.Alerts.CriticalPercent);
    }

    private static void TestUpdateAndPersistProviderVisibility()
    {
        var tempStorage = new InMemorySettingsStorage();
        using var manager = new SettingsManager(tempStorage);

        manager.UpdateProviderVisibility(opt =>
        {
            opt.ClaudeVisible = true;
            opt.CodexVisible = false;
            opt.DeepSeekVisible = true;
            opt.GrokVisible = false;
            opt.CursorVisible = true;
        });

        Assert.True(manager.ProviderVisibility.ClaudeVisible);
        Assert.False(manager.ProviderVisibility.CodexVisible);
        Assert.True(manager.ProviderVisibility.DeepSeekVisible);
        Assert.False(manager.ProviderVisibility.GrokVisible);
        Assert.True(manager.ProviderVisibility.CursorVisible);

        var enabled = tempStorage.Get<List<string>>("AgentIsland.enabledProviders.v1");
        Assert.NotNull(enabled);
        Assert.Contains("claude", enabled!);
        Assert.DoesNotContain("codex", enabled!);
        Assert.Contains("deepseek", enabled!);
        Assert.DoesNotContain("grok", enabled!);
        Assert.Contains("cursor", enabled!);
    }

    private static void TestOptionsMonitorOnChangeFired()
    {
        var tempStorage = new InMemorySettingsStorage();
        using var manager = new SettingsManager(tempStorage);

        int monitorChangeCount = 0;
        using var subscription = manager.PollingMonitor.OnChange((opt, _) =>
        {
            monitorChangeCount++;
        });

        manager.UpdatePolling(opt => opt.RefreshIntervalSeconds = 600);
        manager.UpdatePolling(opt => opt.AutoCheckUpdates = false);

        Assert.Equal(2, monitorChangeCount);
        Assert.Equal(600, manager.PollingMonitor.CurrentValue.RefreshIntervalSeconds);
        Assert.False(manager.PollingMonitor.CurrentValue.AutoCheckUpdates);
    }

    private static void TestStorageExternalChangeSyncsToOptions()
    {
        var tempStorage = new InMemorySettingsStorage();
        using var manager = new SettingsManager(tempStorage);

        bool changed = false;
        using var subscription = manager.AlertMonitor.OnChange((opt, _) =>
        {
            if (opt.AlertsEnabled && opt.WarningPercent == 70)
            {
                changed = true;
            }
        });

        // Simulate external edit (e.g. via Preferences.Set)
        tempStorage.Set("AgentIsland.alertsEnabled", true);
        tempStorage.Set("AgentIsland.alertWarning", 70);

        Assert.True(changed);
        Assert.True(manager.Alerts.AlertsEnabled);
        Assert.Equal(70, manager.Alerts.WarningPercent);
    }

    private sealed class InMemorySettingsStorage : ISettingsStorage
    {
        private readonly Dictionary<string, object> _data = new();
        public event EventHandler<string>? SettingChanged;

        public T? Get<T>(string key)
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

        public bool Has(string key) => _data.ContainsKey(key);

        public void Set<T>(string key, T value)
        {
            if (value is null) _data.Remove(key);
            else _data[key] = value;
            SettingChanged?.Invoke(this, key);
        }

        public void SetBatch(Action<Dictionary<string, System.Text.Json.JsonElement>> update) { }

        public void Remove(string key)
        {
            _data.Remove(key);
            SettingChanged?.Invoke(this, key);
        }
    }
}
