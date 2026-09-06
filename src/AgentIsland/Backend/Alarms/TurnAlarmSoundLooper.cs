using System.Windows.Media;
using System.Windows.Threading;

namespace AgentIsland.Backend.Alarms;

/// Repeats the selected alarm sound every 1.8s until stopped. MediaPlayer
/// (not SoundPlayer) so the store's volume slider actually applies.
public sealed class TurnAlarmSoundLooper
{
    private readonly AgentReminderStore _reminderStore;
    private DispatcherTimer? _timer;
    private MediaPlayer? _player;

    public TurnAlarmSoundLooper(AgentReminderStore? reminderStore = null)
    {
        _reminderStore = reminderStore ?? (App.Instance?.Services?.GetService(typeof(AgentReminderStore)) as AgentReminderStore) ?? new AgentReminderStore();
    }

    public void Start()
    {
        Stop();
        if (!_reminderStore.SoundEnabled) return;
        Play();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _timer.Tick += (_, _) => Play();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _player?.Stop();
        _player = null;
    }

    private void Play()
    {
        if (!_reminderStore.SoundEnabled)
        {
            Stop();
            return;
        }
        // macOS parity: a tick lands while the previous instance is still
        // sounding (long custom files) — skip it rather than overlap, so
        // the effective cadence is max(1.8s, sound length).
        if (_player is { Source: not null } playing
            && playing.NaturalDuration.HasTimeSpan
            && playing.Position < playing.NaturalDuration.TimeSpan)
        {
            return;
        }
        if (_reminderStore.ResolveSoundFile() is not { } file) return;
        try
        {
            _player ??= new MediaPlayer();
            _player.Volume = _reminderStore.Volume;
            _player.Open(new Uri(file));
            _player.Play();
        }
        catch
        {
            // A missing codec or locked file should never take the alarm
            // window down with it.
        }
    }
}
