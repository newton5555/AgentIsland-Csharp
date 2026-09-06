using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentIsland.Backend.Alarms;
using AgentIsland.Backend.Monitoring;
using AgentIsland.Backend.Settings;
using AgentIsland.Core;
using AgentIsland.UI.Localization;
using AgentIsland.UI.Theme;

namespace AgentIsland.UI;

public sealed partial class StatusSettingsPage : UserControl
{
    public static string DescriptionText => L10n.Tr("What the island's two logos are telling you.");
    public static string LogoStatesLabel => L10n.Tr("Logo states").ToUpperInvariant();
    public static string RemindersLabel => L10n.Tr("Reminders").ToUpperInvariant();
    public static string SoundLabel => L10n.Tr("Sound");

    public static string WorkingName => L10n.Tr("Working");
    public static string WorkingCaption => L10n.Tr("The logo rotates while a session is running.");
    public static string YourTurnName => L10n.Tr("Your turn");
    public static string YourTurnCaption => L10n.Tr("A thread finished — Agent Island opens an alarm window so you can reply.");
    public static string AttentionName => L10n.Tr("Needs attention");
    public static string AttentionCaption => L10n.Tr("Limits, login, network, or provider errors make the logo pulse red.");

    public static Geometry CodexPathGeometry => Geometry.Parse("F1 " + BrandGeometry.OpenAiPath);
    public static SolidColorBrush CodexBrush => IslandColors.Brush(IslandColors.Codex);

    public static string DemoSectionLabel => L10n.Tr("Demo — force a state on the island").ToUpperInvariant();
    public static string DemoWorkingLabel => L10n.Tr("Working");
    public static string DemoYourTurnLabel => L10n.Tr("Your turn");
    public static string DemoAuthLabel => L10n.Tr("Auth");
    public static string DemoRateLabel => L10n.Tr("Rate");
    public static string DemoLiveLabel => L10n.Tr("Live");

    public sealed class SoundChoiceItem : INotifyPropertyChanged
    {
        public string Choice { get; init; } = string.Empty;
        public bool IsCustom { get; init; }
        public string Label { get; init; } = string.Empty;
        public string ActionText { get; init; } = string.Empty;
        public Visibility ActionVisibility => IsCustom ? Visibility.Visible : Visibility.Collapsed;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BackgroundBrush)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ForegroundBrush)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckVisibility)));
                }
            }
        }

        public Brush BackgroundBrush => _isSelected
            ? IslandColors.Brush(IslandColors.White(0.055))
            : Brushes.Transparent;

        public Brush ForegroundBrush => _isSelected
            ? IslandColors.Brush(IslandColors.White(0.95))
            : IslandColors.Brush(IslandColors.White(0.72));

        public Visibility CheckVisibility => _isSelected ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static MediaPlayer? _previewPlayer;
    private readonly ObservableCollection<SoundChoiceItem> _soundItems = new();
    private readonly bool _initialized;
    private readonly AgentReminderStore _reminderStore;
    private readonly QuotaAlarmStore _quotaAlarmStore;
    private readonly IActivityMonitor _activityMonitor;

    public StatusSettingsPage() : this(null) { }

    public StatusSettingsPage(
        AgentReminderStore? reminderStore = null,
        QuotaAlarmStore? quotaAlarmStore = null,
        IActivityMonitor? activityMonitor = null)
    {
        var sp = App.Instance?.Services;
        _reminderStore = reminderStore ?? (sp?.GetService(typeof(AgentReminderStore)) as AgentReminderStore) ?? new AgentReminderStore();
        _quotaAlarmStore = quotaAlarmStore ?? (sp?.GetService(typeof(QuotaAlarmStore)) as QuotaAlarmStore) ?? new QuotaAlarmStore();
        _activityMonitor = activityMonitor ?? (sp?.GetService(typeof(IActivityMonitor)) as IActivityMonitor) ?? new ActivityMonitor();

        InitializeComponent();

        WorkingLogo.SetState(ActivityState.Working);
        AttentionLogo.SetState(ActivityState.Stalled);

        var reminderStoreRef = _reminderStore;
        TurnAlarmToggle.IsOn = reminderStoreRef.Enabled;
        TurnAlarmToggle.Toggled += value =>
        {
            if (!_initialized) return;
            reminderStoreRef.Enabled = value;
        };

        AlarmWhenFrontToggle.IsOn = reminderStoreRef.AlarmWhenFrontmost;
        AlarmWhenFrontToggle.Toggled += enabled =>
        {
            if (!_initialized) return;
            reminderStoreRef.AlarmWhenFrontmost = enabled;
        };

        FrontChimeToggle.IsOn = reminderStoreRef.FrontmostSoundOnly;
        FrontChimeToggle.Toggled += enabled =>
        {
            if (!_initialized) return;
            reminderStoreRef.FrontmostSoundOnly = enabled;
        };

        ThreadDetailsToggle.IsOn = reminderStoreRef.ShowSessionDetails;
        ThreadDetailsToggle.Toggled += value =>
        {
            if (!_initialized) return;
            reminderStoreRef.ShowSessionDetails = value;
        };

        var quotaAlarmStoreRef = _quotaAlarmStore;
        QuotaAlarmToggle.IsOn = quotaAlarmStoreRef.Enabled;
        QuotaAlarmToggle.Toggled += value =>
        {
            if (!_initialized) return;
            quotaAlarmStoreRef.Enabled = value;
        };

        AlarmSoundToggle.IsOn = reminderStoreRef.SoundEnabled;
        SoundHost.Visibility = reminderStoreRef.SoundEnabled ? Visibility.Visible : Visibility.Collapsed;
        AlarmSoundToggle.Toggled += value =>
        {
            if (!_initialized) return;
            reminderStoreRef.SoundEnabled = value;
            SoundHost.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        };

        SoundHeaderLabel.Text = CurrentSoundLabel();
        SoundHeader.MouseLeftButtonUp += (_, args) =>
        {
            var isVisible = SoundListContainer.Visibility == Visibility.Visible;
            SoundListContainer.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
            SoundChevron.Text = isVisible ? "⌄" : "⌃";
            args.Handled = true;
        };

        SoundChoicesList.ItemsSource = _soundItems;
        RebuildSoundList();

        VolumeSlider.Value = reminderStoreRef.Volume;
        VolumeSlider.ValueChanged += (_, _) =>
        {
            if (!_initialized) return;
            reminderStoreRef.Volume = VolumeSlider.Value;
        };

        if (AppEnvironment.IsDemo || AppEnvironment.IsDebug)
        {
            DemoSection.Visibility = Visibility.Visible;
            DemoWorkingBtn.Clicked += () => _activityMonitor.Demo(ActivityState.Working);
            DemoYourTurnBtn.Clicked += () => _activityMonitor.Demo(ActivityState.NeedsYou);
            DemoAuthBtn.Clicked += () => _activityMonitor.Demo(ActivityState.AuthRequired);
            DemoRateBtn.Clicked += () => _activityMonitor.Demo(ActivityState.RateLimited);
            DemoLiveBtn.Clicked += () => _activityMonitor.Demo(null);
        }

        _initialized = true;
    }

    private string CurrentSoundLabel()
    {
        var store = _reminderStore;
        if (store.SoundChoice == AgentReminderStore.CustomSoundChoice)
        {
            return store.CustomSoundPath.Length > 0
                ? Path.GetFileName(store.CustomSoundPath)
                : L10n.Tr("Custom file");
        }
        return AgentReminderStore.PresetLabel(store.SoundChoice);
    }

    private void RebuildSoundList()
    {
        _soundItems.Clear();
        var store = _reminderStore;

        foreach (var tone in AgentReminderStore.SystemTones.Available)
        {
            var choice = AgentReminderStore.SystemTones.StoragePrefix + tone.Key;
            _soundItems.Add(new SoundChoiceItem
            {
                Choice = choice,
                IsCustom = false,
                Label = AgentReminderStore.PresetLabel(choice),
                IsSelected = store.SoundChoice == choice,
            });
        }

        foreach (var preset in AgentReminderStore.SoundPresets)
        {
            _soundItems.Add(new SoundChoiceItem
            {
                Choice = preset,
                IsCustom = false,
                Label = AgentReminderStore.PresetLabel(preset),
                IsSelected = store.SoundChoice == preset,
            });
        }

        _soundItems.Add(new SoundChoiceItem
        {
            Choice = AgentReminderStore.CustomSoundChoice,
            IsCustom = true,
            Label = L10n.Tr("Custom file"),
            ActionText = store.CustomSoundPath.Length == 0 ? L10n.Tr("Choose") : L10n.Tr("Change"),
            IsSelected = store.SoundChoice == AgentReminderStore.CustomSoundChoice,
        });
    }

    private void OnSoundRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SoundChoiceItem item })
        {
            var store = _reminderStore;
            if (item.IsCustom)
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Audio|*.wav;*.mp3;*.wma;*.m4a|All files|*.*",
                };
                if (dialog.ShowDialog(Window.GetWindow(this)) == true)
                {
                    store.CustomSoundPath = dialog.FileName;
                    store.SoundChoice = AgentReminderStore.CustomSoundChoice;
                }
            }
            else
            {
                store.SoundChoice = item.Choice;
                PreviewSound();
            }
            SoundHeaderLabel.Text = CurrentSoundLabel();
            RebuildSoundList();
            e.Handled = true;
        }
    }

    private void PreviewSound()
    {
        try
        {
            if (_reminderStore.ResolveSoundFile() is not { } file) return;
            _previewPlayer ??= new MediaPlayer();
            _previewPlayer.Stop();
            _previewPlayer.Volume = _reminderStore.Volume;
            _previewPlayer.Open(new Uri(file));
            _previewPlayer.Play();
        }
        catch
        {
        }
    }
}
