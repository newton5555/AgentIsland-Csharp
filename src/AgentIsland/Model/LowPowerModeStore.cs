using System.ComponentModel;
using System.Runtime.InteropServices;
using AgentIsland.Core;

namespace AgentIsland.Model;

public enum VisualMode
{
    Calm,
    Vivid,
    FollowModel,
}

/// Visual mode: Vivid keeps the ambient glow and orbit sweep in the chosen glow color;
/// Calm is fully clean (no ambient light); FollowModel dynamically streams in the active
/// AI brand colors (and runs dual-comet pursuit when both models are working).
public sealed class LowPowerModeStore : INotifyPropertyChanged
{
    private const string Key = "AgentIsland.lowPowerMode";
    private const string VisualModeKey = "AgentIsland.visualMode";

    public static LowPowerModeStore Shared { get; } = new();

    private VisualMode _mode;
    private bool _systemLowPower;

    public event PropertyChangedEventHandler? PropertyChanged;

    private LowPowerModeStore()
    {
        var rawMode = Preferences.Get<string?>(VisualModeKey);
        if (rawMode is not null)
        {
            _mode = rawMode switch
            {
                "calm" => VisualMode.Calm,
                "follow_model" or "followmodel" => VisualMode.FollowModel,
                _ => VisualMode.Vivid,
            };
        }
        else
        {
            var oldCalm = Preferences.Get<bool?>(Key) ?? false;
            _mode = oldCalm ? VisualMode.Calm : VisualMode.Vivid;
        }

        _systemLowPower = ReadSystemLowPower();
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, _) =>
        {
            var now = ReadSystemLowPower();
            if (now == _systemLowPower) return;
            _systemLowPower = now;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveEnabled)));
        };
    }

    public VisualMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            Preferences.Set(VisualModeKey, value switch
            {
                VisualMode.Calm => "calm",
                VisualMode.FollowModel => "follow_model",
                _ => "vivid",
            });
            Preferences.Set(Key, value == VisualMode.Calm);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Mode)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveEnabled)));
        }
    }

    /// The user's picker choice: true = Calm, false = Vivid / FollowModel.
    public bool Enabled
    {
        get => _mode == VisualMode.Calm;
        set => Mode = value ? VisualMode.Calm : VisualMode.Vivid;
    }

    /// What render gating should read: the system battery saver forces Calm
    /// without touching the stored choice, so Vivid / FollowModel returns when it lifts.
    public bool EffectiveEnabled => _mode == VisualMode.Calm || _systemLowPower;

    // Windows battery saver ("energy saver"): SYSTEM_POWER_STATUS's
    // SystemStatusFlag is 1 while it's on.
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    private static bool ReadSystemLowPower()
    {
        try
        {
            return GetSystemPowerStatus(out var status) && status.SystemStatusFlag == 1;
        }
        catch
        {
            return false;
        }
    }
}
