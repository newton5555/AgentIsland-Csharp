using System.ComponentModel;
using System.Windows.Threading;
using AgentIsland.Core;
using AgentIsland.UI.Providers;
using AgentIsland.Core.Usage;

namespace AgentIsland.Backend.Cost;

/// Publishes per-provider cost rollups from the local logs. Scans run in
/// parallel off the UI thread on the shared poll cadence; demo mode injects
/// the same screenshot-friendly April numbers as macOS.
public sealed class CostStore : INotifyPropertyChanged
{
    public static CostStore Shared { get; } = new();

    private readonly Dictionary<DisplayProvider, ProviderCostSummary> _summaries = new();
    private readonly HashSet<DisplayProvider> _activeProviders = new();
    private DateTimeOffset? _lastUpdated;
    private DispatcherTimer? _pollTimer;
    private PropertyChangedEventHandler? _visibilityChanged;
    private PropertyChangedEventHandler? _intervalChanged;
    private bool _autoRefreshStarted;
    private readonly Dictionary<DisplayProvider, long> _providerModeVersions = new();
    private readonly Dictionary<DisplayProvider, Task<CostScanResult>> _inFlightProviders = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private CostStore()
    {
        foreach (var provider in DisplayProviders.All)
        {
            _summaries[provider] = ProviderCostSummary.Empty;
        }
    }

    /// One provider's rollup, keyed by DisplayProvider. Empty until its first
    /// scan commits, so a caller never sees null and a provider with no local
    /// ledger (Gemini today) reads as honest zeros rather than a fabricated $0.
    public ProviderCostSummary Summary(DisplayProvider provider) =>
        _summaries.TryGetValue(provider, out var summary) ? summary : ProviderCostSummary.Empty;

    public ProviderCostSummary Claude => Summary(DisplayProvider.Claude);
    public ProviderCostSummary Codex => Summary(DisplayProvider.Codex);
    public ProviderCostSummary DeepSeek => Summary(DisplayProvider.DeepSeek);
    public DateTimeOffset? LastUpdated { get => _lastUpdated; private set { _lastUpdated = value; Raise(nameof(LastUpdated)); } }

    private void SetSummary(DisplayProvider provider, ProviderCostSummary summary)
    {
        _summaries[provider] = summary;
        // Keep the two named accessors' change notifications so existing
        // subscribers (OverviewPage, report cards) refresh exactly as before;
        // guest tiles ride the LastUpdated notification the commit also raises.
        if (provider == DisplayProvider.Claude) Raise(nameof(Claude));
        else if (provider == DisplayProvider.Codex) Raise(nameof(Codex));
        else if (provider == DisplayProvider.DeepSeek) Raise(nameof(DeepSeek));
    }

    public void StartAutoRefresh()
    {
        if (_autoRefreshStarted) return;
        _autoRefreshStarted = true;
        _visibilityChanged = OnProviderVisibilityChanged;
        AgentIsland.Backend.Settings.ProviderVisibilityStore.Shared.PropertyChanged += _visibilityChanged;
        _intervalChanged = OnRefreshIntervalChanged;
        RefreshIntervalStore.Shared.PropertyChanged += _intervalChanged;
        ApplyProviderMode();
    }

    public void StopAutoRefresh()
    {
        if (!_autoRefreshStarted) return;
        _autoRefreshStarted = false;
        _pollTimer?.Stop();
        _pollTimer = null;
        if (_visibilityChanged is not null)
        {
            AgentIsland.Backend.Settings.ProviderVisibilityStore.Shared.PropertyChanged -= _visibilityChanged;
            _visibilityChanged = null;
        }
        if (_intervalChanged is not null)
        {
            RefreshIntervalStore.Shared.PropertyChanged -= _intervalChanged;
            _intervalChanged = null;
        }
        foreach (var provider in _activeProviders)
        {
            CostQueryService.Shared.Invalidate(provider);
            ClearProviderMemory(provider);
        }
        _inFlightProviders.Clear();
    }

    private void OnRefreshIntervalChanged(object? sender, PropertyChangedEventArgs args) => ArmPollTimer();

    private void OnProviderVisibilityChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(AgentIsland.Backend.Settings.ProviderVisibilityStore.Enabled)) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(ApplyProviderMode);
            return;
        }
        ApplyProviderMode();
    }

    private void ApplyProviderMode()
    {
        var next = AgentIsland.Backend.Settings.ProviderVisibilityStore.Shared.Enabled.ToHashSet();
        var changed = !_activeProviders.SetEquals(next);
        var removed = _activeProviders.Except(next).ToArray();
        var changedProviders = _activeProviders
            .Concat(next)
            .Distinct()
            .Where(provider => _activeProviders.Contains(provider) != next.Contains(provider))
            .ToArray();
        _activeProviders.Clear();
        foreach (var provider in next) _activeProviders.Add(provider);

        if (changed)
        {
            foreach (var provider in changedProviders)
            {
                _providerModeVersions.TryGetValue(provider, out var version);
                _providerModeVersions[provider] = version + 1;
            }
            foreach (var provider in removed)
            {
                SetSummary(provider, ProviderCostSummary.Empty);
                CostQueryService.Shared.Invalidate(provider);
                _inFlightProviders.Remove(provider);
                ClearProviderMemory(provider);
            }
            if (_activeProviders.Count == 0) LastUpdated = null;
        }

        if (!_autoRefreshStarted) return;
        if (_activeProviders.Count == 0)
        {
            _pollTimer?.Stop();
            _pollTimer = null;
            return;
        }
        ArmPollTimer();
        Refresh();
    }

    private void ArmPollTimer()
    {
        if (!_autoRefreshStarted || _activeProviders.Count == 0) return;
        _pollTimer?.Stop();
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(RefreshIntervalStore.Shared.Seconds),
        };
        _pollTimer.Tick += (_, _) => Refresh();
        _pollTimer.Start();
    }

    public void Refresh()
    {
        if (AppEnvironment.IsDemo)
        {
            InjectDemoData();
            return;
        }
        if (_activeProviders.Count == 0) return;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var now = DateTimeOffset.Now;
        var lookback = CostSummarizer.YearHistoryDays(now);
        foreach (var provider in _activeProviders.ToArray())
        {
            // CostQueryService coalesces with a report query already reading
            // this provider. CostStore attaches only once, so one completion
            // cannot publish the same summary repeatedly on every timer tick.
            if (_inFlightProviders.ContainsKey(provider)) continue;
            var providerModeVersion = _providerModeVersions.TryGetValue(provider, out var version)
                ? version
                : 0;
            var task = CostQueryService.Shared.ScanAsync(provider, lookback, now);
            _inFlightProviders[provider] = task;
            _ = ObserveProviderScan(provider, providerModeVersion, task, dispatcher);
        }
    }

    private async Task ObserveProviderScan(
        DisplayProvider provider,
        long providerModeVersion,
        Task<CostScanResult> task,
        Dispatcher dispatcher)
    {
        CostScanResult? result = null;
        try
        {
            result = await task.ConfigureAwait(false);
        }
        catch
        {
            // Cancellation, a torn log, or a provider-specific fault leaves
            // the prior good summary in place and releases the latch below.
        }

        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (result is not null
                        && providerModeVersion == CurrentProviderModeVersion(provider)
                        && _activeProviders.Contains(provider)
                        && CostQueryService.Shared.IsCurrent(provider, result.ProviderVersion))
                    {
                        SetSummary(provider, result.Summary);
                        LastUpdated = DateTimeOffset.Now;
                    }
                }
                finally
                {
                    if (_inFlightProviders.TryGetValue(provider, out var current)
                        && ReferenceEquals(current, task))
                    {
                        _inFlightProviders.Remove(provider);
                    }
                }
                if (providerModeVersion != CurrentProviderModeVersion(provider)
                    && _activeProviders.Contains(provider))
                {
                    Refresh();
                }
            });
        }
        catch
        {
            // The app dispatcher may be shutting down. The task is already
            // complete and no state needs to be published during exit.
            if (_inFlightProviders.TryGetValue(provider, out var current)
                && ReferenceEquals(current, task))
            {
                _inFlightProviders.Remove(provider);
            }
        }
    }

    private long CurrentProviderModeVersion(DisplayProvider provider) =>
        _providerModeVersions.TryGetValue(provider, out var version) ? version : 0;

    private static void ClearProviderMemory(DisplayProvider provider)
    {
        switch (provider)
        {
            case DisplayProvider.Claude:
                ClaudeLogReader.ClearMemoryCache();
                break;
            case DisplayProvider.Codex:
                CodexLogReader.ClearMemoryCache();
                break;
            case DisplayProvider.DeepSeek:
                DeepSeekLogReader.ClearMemoryCache();
                break;
        }
    }

    /// Same hand-tuned April dataset the macOS demo ships: cache-heavy
    /// Claude (10% billable ratio), higher-billable Codex (20%).
    private void InjectDemoData()
    {
        var now = DateTimeOffset.Now;
        foreach (var provider in DisplayProviders.All.Where(provider => !_activeProviders.Contains(provider)))
            SetSummary(provider, ProviderCostSummary.Empty);
        if (_activeProviders.Contains(DisplayProvider.Claude))
        {
            SetSummary(DisplayProvider.Claude,
                DemoSummary(now, 146.61, 211_240_000, 21_120_000, 1_510.80, 2_170_000_000, 217_100_000, seed: 7));
        }
        if (_activeProviders.Contains(DisplayProvider.Codex))
        {
            SetSummary(DisplayProvider.Codex,
                DemoSummary(now, 136.50, 164_120_000, 32_820_000, 1_342.60, 1_610_000_000, 322_860_000, seed: 21));
        }
        LastUpdated = _activeProviders.Count > 0 ? now : null;
    }

    private static ProviderCostSummary DemoSummary(
        DateTimeOffset now,
        double todayDollars, long todayTokens, long todayBillable,
        double monthDollars, long monthTokens, long monthBillable,
        int seed = 7)
    {
        var hourly = new double[24];
        var progress = Math.Max(1, now.Hour);
        for (var h = 0; h <= now.Hour && h < 24; h++)
        {
            var t = (double)h / progress;
            hourly[h] = todayDollars * (0.15 + 0.85 * t * t);
        }
        for (var h = now.Hour + 1; h < 24; h++) hourly[h] = todayDollars;
        var dayCount = now.Day;
        var dailySeries = new double[dayCount];
        for (var d = 0; d < dayCount; d++)
        {
            dailySeries[d] = monthDollars * (d + 1) / dayCount;
        }
        // Sparse, believable year: quiet start, dense spring/summer — the
        // shape the real product screenshots show. Per-provider seeds keep
        // the two histories from overlapping every day (which would render
        // the whole grid as split cells).
        var history = new List<DailyTokenBucket>();
        var random = new Random(seed);
        for (var day = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, now.Offset); day <= now; day = day.AddDays(1))
        {
            var density = day.Month switch
            {
                <= 2 => 0.05,
                3 => 0.3,
                >= 4 => 0.75,
            };
            if (random.NextDouble() > density) continue;
            var tokens = (long)(monthTokens / 30.0 * (0.2 + random.NextDouble()));
            history.Add(new DailyTokenBucket(day, tokens, tokens / 10, tokens / 1_500_000.0));
        }
        return new ProviderCostSummary(
            todayDollars, todayTokens, todayBillable,
            monthDollars, monthTokens, monthBillable,
            hourly, dailySeries,
            new[] { new ModelSpend("claude-fable-5", todayTokens / 3, todayBillable / 3, todayDollars / 3) },
            new[] { new ModelSpend("claude-fable-5", todayTokens, todayBillable, todayDollars) },
            new[] { new ModelSpend("claude-fable-5", monthTokens, monthBillable, monthDollars) },
            history,
            Array.Empty<string>());
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
