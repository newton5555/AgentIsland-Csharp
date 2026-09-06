using AgentIsland.Core.Options;
using AgentIsland.Core.Storage;
using AgentIsland.Windows;
using Microsoft.Extensions.Options;

namespace AgentIsland.Backend.Settings;

/// <summary>
/// Central coordinator providing strongly-typed options and IOptionsMonitor adapters
/// on top of atomic JSON settings storage.
/// </summary>
public sealed class SettingsManager : ISettingsManager, IDisposable
{
    private readonly ISettingsStorage _storage;
    private readonly SettingsOptionsMonitor<IslandDisplayOptions> _displayMonitor;
    private readonly SettingsOptionsMonitor<PollingOptions> _pollingMonitor;
    private readonly SettingsOptionsMonitor<AlertOptions> _alertMonitor;
    private readonly SettingsOptionsMonitor<ProviderVisibilityOptions> _visibilityMonitor;

    public event EventHandler<IslandDisplayOptions>? DisplayChanged;
    public event EventHandler<PollingOptions>? PollingChanged;
    public event EventHandler<AlertOptions>? AlertsChanged;
    public event EventHandler<ProviderVisibilityOptions>? ProviderVisibilityChanged;

    public IOptionsMonitor<IslandDisplayOptions> DisplayMonitor => _displayMonitor;
    public IOptionsMonitor<PollingOptions> PollingMonitor => _pollingMonitor;
    public IOptionsMonitor<AlertOptions> AlertMonitor => _alertMonitor;
    public IOptionsMonitor<ProviderVisibilityOptions> ProviderVisibilityMonitor => _visibilityMonitor;

    public SettingsManager() : this(Preferences.Storage) { }

    public SettingsManager(ISettingsStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));

        _displayMonitor = new SettingsOptionsMonitor<IslandDisplayOptions>(ReadDisplayOptions);
        _pollingMonitor = new SettingsOptionsMonitor<PollingOptions>(ReadPollingOptions);
        _alertMonitor = new SettingsOptionsMonitor<AlertOptions>(ReadAlertOptions);
        _visibilityMonitor = new SettingsOptionsMonitor<ProviderVisibilityOptions>(ReadProviderVisibilityOptions);

        _storage.SettingChanged += OnSettingChanged;
    }

    private bool _isUpdating;

    public IslandDisplayOptions Display => _displayMonitor.CurrentValue;
    public PollingOptions Polling => _pollingMonitor.CurrentValue;
    public AlertOptions Alerts => _alertMonitor.CurrentValue;
    public ProviderVisibilityOptions ProviderVisibility => _visibilityMonitor.CurrentValue;

    public void UpdateDisplay(Action<IslandDisplayOptions> configure)
    {
        var options = ReadDisplayOptions();
        configure(options);

        _isUpdating = true;
        try
        {
            _storage.Set("AgentIsland.spacingMode", options.SpacingMode.ToString());
            _storage.Set("AgentIsland.spacingScale", options.SpacingScale);
            _storage.Set("AgentIsland.alwaysShowUsage", options.AlwaysShowUsage);
            _storage.Set("AgentIsland.glowColor", options.GlowColor);
            _storage.Set("AgentIsland.showCostPanelPage", options.ShowCostPanelPage);
        }
        finally
        {
            _isUpdating = false;
        }

        _displayMonitor.NotifyChanged();
        DisplayChanged?.Invoke(this, options);
    }

    public void UpdatePolling(Action<PollingOptions> configure)
    {
        var options = ReadPollingOptions();
        configure(options);

        var clampedSeconds = Math.Clamp(options.RefreshIntervalSeconds, 60, 3600);
        _isUpdating = true;
        try
        {
            _storage.Set("AgentIsland.refreshInterval", clampedSeconds);
            _storage.Set("AgentIsland.pollInterval", clampedSeconds);
            _storage.Set("AgentIsland.autoCheckUpdates", options.AutoCheckUpdates);
        }
        finally
        {
            _isUpdating = false;
        }

        _pollingMonitor.NotifyChanged();
        PollingChanged?.Invoke(this, options);
    }

    public void UpdateAlerts(Action<AlertOptions> configure)
    {
        var options = ReadAlertOptions();
        configure(options);

        _isUpdating = true;
        try
        {
            _storage.Set("AgentIsland.alertsEnabled", options.AlertsEnabled);
            _storage.Set("AgentIsland.alertWarning", Math.Clamp(options.WarningPercent, 50, 98));
            _storage.Set("AgentIsland.alertCritical", Math.Clamp(options.CriticalPercent, 51, 99));
        }
        finally
        {
            _isUpdating = false;
        }

        _alertMonitor.NotifyChanged();
        AlertsChanged?.Invoke(this, options);
    }

    public void UpdateProviderVisibility(Action<ProviderVisibilityOptions> configure)
    {
        var options = ReadProviderVisibilityOptions();
        configure(options);

        var list = new List<string>();
        if (options.ClaudeVisible) list.Add("claude");
        if (options.CodexVisible) list.Add("codex");
        if (options.DeepSeekVisible) list.Add("deepseek");
        if (options.GrokVisible) list.Add("grok");
        if (options.CursorVisible) list.Add("cursor");

        _isUpdating = true;
        try
        {
            _storage.Set("AgentIsland.enabledProviders.v1", list);
        }
        finally
        {
            _isUpdating = false;
        }

        _visibilityMonitor.NotifyChanged();
        ProviderVisibilityChanged?.Invoke(this, options);
    }

    private IslandDisplayOptions ReadDisplayOptions()
    {
        var rawMode = _storage.Get<string?>("AgentIsland.spacingMode");
        var mode = Enum.TryParse<IslandSpacingMode>(rawMode, true, out var parsed) ? parsed : IslandSpacingMode.NotchStyle;

        return new IslandDisplayOptions
        {
            SpacingMode = mode,
            SpacingScale = _storage.Get<double?>("AgentIsland.spacingScale") ?? 1.0,
            AlwaysShowUsage = _storage.Get<bool?>("AgentIsland.alwaysShowUsage") ?? false,
            GlowColor = _storage.Get<string?>("AgentIsland.glowColor") ?? "teal",
            ShowCostPanelPage = _storage.Get<bool?>("AgentIsland.showCostPanelPage") ?? false,
        };
    }

    private PollingOptions ReadPollingOptions()
    {
        var interval = _storage.Get<int?>("AgentIsland.refreshInterval")
            ?? _storage.Get<int?>("AgentIsland.pollInterval")
            ?? 300;

        return new PollingOptions
        {
            RefreshIntervalSeconds = interval,
            AutoCheckUpdates = _storage.Get<bool?>("AgentIsland.autoCheckUpdates") ?? true,
        };
    }

    private AlertOptions ReadAlertOptions()
    {
        return new AlertOptions
        {
            AlertsEnabled = _storage.Get<bool?>("AgentIsland.alertsEnabled") ?? false,
            WarningPercent = Math.Clamp(_storage.Get<int?>("AgentIsland.alertWarning") ?? 80, 50, 98),
            CriticalPercent = Math.Clamp(_storage.Get<int?>("AgentIsland.alertCritical") ?? 95, 51, 99),
        };
    }

    private ProviderVisibilityOptions ReadProviderVisibilityOptions()
    {
        var enabled = _storage.Get<List<string>?>("AgentIsland.enabledProviders.v1");
        if (enabled != null)
        {
            return new ProviderVisibilityOptions
            {
                ClaudeVisible = enabled.Contains("claude", StringComparer.OrdinalIgnoreCase),
                CodexVisible = enabled.Contains("codex", StringComparer.OrdinalIgnoreCase),
                DeepSeekVisible = enabled.Contains("deepseek", StringComparer.OrdinalIgnoreCase),
                GrokVisible = enabled.Contains("grok", StringComparer.OrdinalIgnoreCase),
                CursorVisible = enabled.Contains("cursor", StringComparer.OrdinalIgnoreCase),
            };
        }

        return new ProviderVisibilityOptions
        {
            ClaudeVisible = _storage.Get<bool?>("AgentIsland.claudeVisible") ?? true,
            CodexVisible = _storage.Get<bool?>("AgentIsland.codexVisible") ?? true,
            DeepSeekVisible = true,
            GrokVisible = true,
            CursorVisible = true,
        };
    }

    private void OnSettingChanged(object? sender, string key)
    {
        if (_isUpdating) return;

        if (key.StartsWith("AgentIsland.spacing") || key == "AgentIsland.alwaysShowUsage" || key == "AgentIsland.glowColor" || key == "AgentIsland.showCostPanelPage")
        {
            _displayMonitor.NotifyChanged();
            DisplayChanged?.Invoke(this, Display);
        }
        else if (key == "AgentIsland.refreshInterval" || key == "AgentIsland.pollInterval" || key == "AgentIsland.autoCheckUpdates")
        {
            _pollingMonitor.NotifyChanged();
            PollingChanged?.Invoke(this, Polling);
        }
        else if (key.StartsWith("AgentIsland.alert"))
        {
            _alertMonitor.NotifyChanged();
            AlertsChanged?.Invoke(this, Alerts);
        }
        else if (key.Contains("provider") || key.EndsWith("Visible"))
        {
            _visibilityMonitor.NotifyChanged();
            ProviderVisibilityChanged?.Invoke(this, ProviderVisibility);
        }
    }

    public void Dispose()
    {
        _storage.SettingChanged -= OnSettingChanged;
        _displayMonitor.Dispose();
        _pollingMonitor.Dispose();
        _alertMonitor.Dispose();
        _visibilityMonitor.Dispose();
    }
}
