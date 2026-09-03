using System.ComponentModel;
using System.IO;
using AgentIsland.Core;

namespace AgentIsland.Alarm;

/// Turn-alarm preferences: master switch, sound on/off, volume, sound
/// choice (Windows Media presets or a custom file), and whether the alarm
/// window shows session details.
public sealed class AgentReminderStore : INotifyPropertyChanged
{
    /// Built-in presets, synthesized to match the character of the macOS
    /// alert-sound palette — the full 14-name list the Mac app offers, in
    /// the same order. Keys are stable; the display names localize
    /// (低音, 吹气, 瓶子, …). Declared before Shared: static initializers
    /// run in declaration order, and the instance ctor reads this array.
    public static readonly string[] SoundPresets =
    {
        "Basso", "Blow", "Bottle", "Frog", "Funk", "Glass", "Hero",
        "Morse", "Ping", "Pop", "Purr", "Sosumi", "Submarine", "Tink",
    };

    /// Windows' own alarm library — the platform mirror of the macOS
    /// Apple-ringtone tier (2.1.2). C:\Windows\Media ships the ten
    /// Alarm01–Alarm10 tones the Clock app uses; they are referenced in
    /// place at runtime and never bundled — they are Microsoft's audio,
    /// and they are already on the disk. If a future Windows moves them
    /// the tier simply disappears and the synthesized presets remain.
    public static class SystemTones
    {
        public const string StoragePrefix = "SystemTone:";

        public static string Directory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

        /// Chimes (Alarm01) leads — the Clock app's default alarm, the one
        /// everyone knows. Names are the Clock app's own labels for the
        /// ten files, in file order.
        public static readonly (string Key, string Label)[] Curated =
        {
            ("Alarm01", "Chimes"), ("Alarm02", "Xylophone"), ("Alarm03", "Chords"),
            ("Alarm04", "Taps"), ("Alarm05", "Jingle"), ("Alarm06", "Transition"),
            ("Alarm07", "Descent"), ("Alarm08", "Bounce"), ("Alarm09", "Echo"),
            ("Alarm10", "Serenity"),
        };

        public static string PathFor(string key) => Path.Combine(Directory, key + ".wav");

        public static IEnumerable<(string Key, string Label)> Available =>
            Curated.Where(tone => File.Exists(PathFor(tone.Key)));

        public static string? LabelFor(string key) =>
            Curated.FirstOrDefault(tone => tone.Key == key).Label;
    }

    public static AgentReminderStore Shared { get; } = new();

    private const string EnabledKey = "AgentIsland.agentReminders";
    private const string SoundEnabledKey = "AgentIsland.agentReminderSound";
    private const string VolumeKey = "AgentIsland.agentReminderVolume";
    private const string SoundChoiceKey = "AgentIsland.agentReminderSoundChoice";
    private const string CustomSoundKey = "AgentIsland.agentReminderCustomSound";
    private const string ShowDetailsKey = "AgentIsland.agentReminderShowSessionDetails";
    private const string AlarmWhenFrontmostKey = "AgentIsland.agentReminderAlarmWhenFrontmost";
    private const string FrontmostSoundOnlyKey = "AgentIsland.agentReminderFrontmostSoundOnly";

    public const string CustomSoundChoice = "Custom";

    private bool _enabled;
    private bool _soundEnabled;
    private double _volume;
    private string _soundChoice;
    private string _customSoundPath;
    private bool _showSessionDetails;
    private bool _alarmWhenFrontmost;
    private bool _frontmostSoundOnly;

    public event PropertyChangedEventHandler? PropertyChanged;

    private AgentReminderStore()
    {
        _enabled = Preferences.Get<bool?>(EnabledKey) ?? true;
        _soundEnabled = Preferences.Get<bool?>(SoundEnabledKey) ?? true;
        _volume = Math.Clamp(Preferences.Get<double?>(VolumeKey) ?? 0.8, 0, 1);
        var stored = Preferences.Get<string?>(SoundChoiceKey);
        if (stored is null)
        {
            // Fresh install: the system alarm tier leads, Chimes first —
            // the alarm should sound like the platform's own (the macOS
            // build defaults to Radar for the same reason).
            _soundChoice = SystemTones.Available.FirstOrDefault() is { Key.Length: > 0 } first
                ? SystemTones.StoragePrefix + first.Key
                : "Glass";
        }
        else if (stored.StartsWith(SystemTones.StoragePrefix, StringComparison.Ordinal))
        {
            // A tone this system lacks (synced prefs, OS change) must not
            // resolve — fall back rather than ring silent.
            var key = stored[SystemTones.StoragePrefix.Length..];
            _soundChoice = File.Exists(SystemTones.PathFor(key)) ? stored : "Glass";
        }
        else if (stored != CustomSoundChoice && !SoundPresets.Contains(stored))
        {
            _soundChoice = "Glass";
        }
        else
        {
            _soundChoice = stored;
        }
        _customSoundPath = Preferences.Get<string?>(CustomSoundKey) ?? "";
        _showSessionDetails = Preferences.Get<bool?>(ShowDetailsKey) ?? false;
        _alarmWhenFrontmost = Preferences.Get<bool?>(AlarmWhenFrontmostKey) ?? false;
        _frontmostSoundOnly = Preferences.Get<bool?>(FrontmostSoundOnlyKey) ?? false;
    }

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; Preferences.Set(EnabledKey, value); Raise(nameof(Enabled)); }
    }

    public bool SoundEnabled
    {
        get => _soundEnabled;
        set { _soundEnabled = value; Preferences.Set(SoundEnabledKey, value); Raise(nameof(SoundEnabled)); }
    }

    public double Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0, 1); Preferences.Set(VolumeKey, _volume); Raise(nameof(Volume)); }
    }

    public string SoundChoice
    {
        get => _soundChoice;
        set { _soundChoice = value; Preferences.Set(SoundChoiceKey, value); Raise(nameof(SoundChoice)); }
    }

    public string CustomSoundPath
    {
        get => _customSoundPath;
        set { _customSoundPath = value; Preferences.Set(CustomSoundKey, value); Raise(nameof(CustomSoundPath)); }
    }

    public bool ShowSessionDetails
    {
        get => _showSessionDetails;
        set { _showSessionDetails = value; Preferences.Set(ShowDetailsKey, value); Raise(nameof(ShowSessionDetails)); }
    }

    /// Whether a finished turn still raises its alarm while the session's
    /// own app is frontmost. Off by default: watching the turn finish IS
    /// the notification (macOS owner call, 2026-08-08).
    public bool AlarmWhenFrontmost
    {
        get => _alarmWhenFrontmost;
        set { _alarmWhenFrontmost = value; Preferences.Set(AlarmWhenFrontmostKey, value); Raise(nameof(AlarmWhenFrontmost)); }
    }

    /// The #9 chime: when the frontmost hold swallows an alarm, play one
    /// chime at that moment instead of total silence. Opt-in.
    public bool FrontmostSoundOnly
    {
        get => _frontmostSoundOnly;
        set { _frontmostSoundOnly = value; Preferences.Set(FrontmostSoundOnlyKey, value); Raise(nameof(FrontmostSoundOnly)); }
    }

    /// Resolves the current choice to a playable file, or null when nothing
    /// usable exists (sound then simply stays silent). Presets synthesize
    /// on first use.
    public string? ResolveSoundFile()
    {
        if (_soundChoice == CustomSoundChoice)
        {
            return File.Exists(_customSoundPath) ? _customSoundPath : null;
        }
        if (_soundChoice.StartsWith(SystemTones.StoragePrefix, StringComparison.Ordinal))
        {
            var path = SystemTones.PathFor(_soundChoice[SystemTones.StoragePrefix.Length..]);
            return File.Exists(path) ? path : SoundSynth.EnsurePreset("Glass");
        }
        return SoundSynth.EnsurePreset(_soundChoice);
    }

    /// Localized display name for a preset key — the zh names mirror the
    /// macOS sound list (低音, 吹气, 瓶子, …).
    public static string PresetLabel(string key)
    {
        if (key.StartsWith(SystemTones.StoragePrefix, StringComparison.Ordinal))
        {
            var toneKey = key[SystemTones.StoragePrefix.Length..];
            return SystemTones.LabelFor(toneKey) ?? toneKey;
        }
        if (!Localization.L10n.IsChinese) return key;
        return key switch
        {
            "Basso" => "低音",
            "Blow" => "吹气",
            "Bottle" => "瓶子",
            "Frog" => "青蛙",
            "Funk" => "放克",
            "Glass" => "玻璃",
            "Hero" => "英雄",
            "Morse" => "摩斯",
            "Ping" => "叮",
            "Pop" => "泡泡",
            "Purr" => "呼噜",
            "Sosumi" => "嗖咪",
            "Submarine" => "水下",
            "Tink" => "叮当",
            _ => key,
        };
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
