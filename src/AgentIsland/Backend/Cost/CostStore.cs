using System.ComponentModel;
using System.Windows.Threading;
using AgentIsland.Core;
using AgentIsland.UI.Providers;
using AgentIsland.Core.Usage;
using AgentIsland.Windows.Memory;
using AgentIsland.Backend.Settings;

namespace AgentIsland.Backend.Cost;

/// Publishes per-provider cost rollups from the local logs. Scans run in
/// parallel off the UI thread on the shared poll cadence; demo mode injects
/// the same screenshot-friendly April numbers as macOS.
public sealed class CostStore : ICostStore
{
    private readonly Dictionary<DisplayProvider, ProviderCostSummary> _summaries = new();
    private readonly HashSet<DisplayProvider> _activeProviders = new();
    private DateTimeOffset? _lastUpdated;
    private DispatcherTimer? _pollTimer;
    private PropertyChangedEventHandler? _visibilityChanged;
    private PropertyChangedEventHandler? _intervalChanged;
    private bool _autoRefreshStarted;
    private sealed class CostInFlight
    {
        public long Version;
        public Task<CostScanResult> ScanTask = null!;
        public TaskCompletionSource CompletionTcs = null!;
    }

    private readonly Dictionary<DisplayProvider, long> _providerModeVersions = new();
    private readonly Dictionary<DisplayProvider, CostInFlight> _inFlightProviders = new();

    private readonly Dictionary<DisplayProvider, AgentIsland.Core.Agents.ICostLedgerReader> _injectedReaders = new();
    private readonly IProviderVisibilityStore? _visibilityStore;
    private readonly RefreshIntervalStore? _intervalStore;
    private readonly ICostQueryService? _costQueryService;
    private readonly AgentIsland.Core.Threading.IUiDispatcher _uiDispatcher;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CostStore(
        IProviderVisibilityStore? visibilityStore = null,
        RefreshIntervalStore? intervalStore = null,
        ICostQueryService? costQueryService = null,
        AgentIsland.Core.Threading.IUiDispatcher? uiDispatcher = null,
        IEnumerable<AgentIsland.Core.Agents.IAgentProvider>? providers = null)
    {
        _visibilityStore = visibilityStore;
        _intervalStore = intervalStore;
        _costQueryService = costQueryService;
        _uiDispatcher = uiDispatcher ?? (System.Windows.Application.Current?.Dispatcher is not null
            ? new AgentIsland.UI.Threading.WpfUiDispatcher()
            : AgentIsland.Core.Threading.DirectUiDispatcher.Instance);

        foreach (var provider in DisplayProviders.All)
        {
            _summaries[provider] = ProviderCostSummary.Empty;
        }

        if (providers is not null)
        {
            foreach (var p in providers)
            {
                if (p.CostLedgerReader is null) continue;
                var dp = DisplayProviders.Parse(p.Descriptor.Key.Value);
                if (dp is not null)
                {
                    _injectedReaders[dp.Value] = p.CostLedgerReader;
                }
            }
        }

        ApplyProviderMode();
    }

    public ProviderCostSummary Summary(DisplayProvider provider) =>
        _summaries.TryGetValue(provider, out var summary) ? summary : ProviderCostSummary.Empty;

    public ProviderCostSummary Claude => Summary(DisplayProvider.Claude);
    public ProviderCostSummary Codex => Summary(DisplayProvider.Codex);
    public ProviderCostSummary DeepSeek => Summary(DisplayProvider.DeepSeek);

    public DateTimeOffset? LastUpdated
    {
        get => _lastUpdated;
        private set
        {
            if (_lastUpdated == value) return;
            _lastUpdated = value;
            Raise(nameof(LastUpdated));
        }
    }

    private void SetSummary(DisplayProvider provider, ProviderCostSummary value)
    {
        _summaries[provider] = value;
        // Keep the two named accessors' change notifications so existing
        // subscribers (OverviewPage, report cards) refresh exactly as before;
        // guest tiles ride the LastUpdated notification the commit also raises.
        if (provider == DisplayProvider.Claude) Raise(nameof(Claude));
        else if (provider == DisplayProvider.Codex) Raise(nameof(Codex));
        else if (provider == DisplayProvider.DeepSeek) Raise(nameof(DeepSeek));
    }

    public void StartAutoRefresh()
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(StartAutoRefresh);
            return;
        }

        if (_autoRefreshStarted) return;
        _autoRefreshStarted = true;
        _visibilityChanged = OnProviderVisibilityChanged;
        if (_visibilityStore != null)
        {
            _visibilityStore.PropertyChanged += _visibilityChanged;
        }
        _intervalChanged = OnRefreshIntervalChanged;
        if (_intervalStore != null)
        {
            _intervalStore.PropertyChanged += _intervalChanged;
        }
        ApplyProviderMode();
        Refresh();
    }

    public void StopAutoRefresh()
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(StopAutoRefresh);
            return;
        }

        if (!_autoRefreshStarted) return;
        _autoRefreshStarted = false;
        _pollTimer?.Stop();
        _pollTimer = null;
        if (_visibilityChanged is not null && _visibilityStore is not null)
        {
            _visibilityStore.PropertyChanged -= _visibilityChanged;
            _visibilityChanged = null;
        }
        if (_intervalChanged is not null && _intervalStore is not null)
        {
            _intervalStore.PropertyChanged -= _intervalChanged;
            _intervalChanged = null;
        }
        foreach (var provider in _activeProviders)
        {
            _providerModeVersions.TryGetValue(provider, out var version);
            _providerModeVersions[provider] = version + 1;
            _costQueryService?.Invalidate(provider);
            ClearProviderMemory(provider);
        }
        foreach (var inFlight in _inFlightProviders.Values)
        {
            inFlight.CompletionTcs.TrySetResult();
        }
        _inFlightProviders.Clear();
        MemoryReclaimer.ScheduleReclaim();
    }

    private void OnRefreshIntervalChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(() => ArmPollTimer());
            return;
        }
        ArmPollTimer();
    }

    private void OnProviderVisibilityChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ProviderVisibilityStore.Enabled)) return;
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(() =>
            {
                if (ApplyProviderMode() && _activeProviders.Count > 0) Refresh();
            });
            return;
        }
        if (ApplyProviderMode() && _activeProviders.Count > 0) Refresh();
    }

    private bool ApplyProviderMode()
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(() => ApplyProviderMode());
            return false;
        }

        var enabled = _visibilityStore?.Enabled ?? (IReadOnlyList<DisplayProvider>)DisplayProviders.All;
        var next = enabled.ToHashSet();
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
                _costQueryService?.Invalidate(provider);
                if (_inFlightProviders.Remove(provider, out var inFlight))
                {
                    inFlight.CompletionTcs.TrySetResult();
                }
                ClearProviderMemory(provider);
            }
            if (removed.Length > 0)
            {
                MemoryReclaimer.ScheduleReclaim();
            }
            if (_activeProviders.Count == 0) LastUpdated = null;
        }

        if (!_autoRefreshStarted) return changed;
        if (_activeProviders.Count == 0)
        {
            _pollTimer?.Stop();
            _pollTimer = null;
            return changed;
        }
        ArmPollTimer();
        return changed;
    }

    public bool DisableInternalTimer { get; set; }

    private void ArmPollTimer()
    {
        if (!_autoRefreshStarted || _activeProviders.Count == 0) return;
        _pollTimer?.Stop();
        if (DisableInternalTimer) return;
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(_intervalStore?.Seconds ?? 300),
        };
        _pollTimer.Tick += (_, _) => Refresh();
        _pollTimer.Start();
    }

    public void Refresh()
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(Refresh);
            return;
        }

        if (AppEnvironment.IsDemo)
        {
            InjectDemoData();
            return;
        }
        ApplyProviderMode();
        if (_activeProviders.Count == 0) return;

        var now = DateTimeOffset.Now;
        var lookback = CostSummarizer.YearHistoryDays(now);
        foreach (var provider in _activeProviders.ToArray())
        {
            if (_inFlightProviders.ContainsKey(provider)) continue;
            var providerModeVersion = CurrentProviderModeVersion(provider);
            var queryService = _costQueryService ?? new CostQueryService(_visibilityStore ?? new ProviderVisibilityStore());
            var task = queryService.ScanAsync(provider, lookback, now);
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _inFlightProviders[provider] = new CostInFlight
            {
                Version = providerModeVersion,
                ScanTask = task,
                CompletionTcs = tcs
            };

            _ = ObserveProviderScan(provider, providerModeVersion, task, tcs, queryService);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Task[] inFlight;
        if (_uiDispatcher.CheckAccess())
        {
            Refresh();
            inFlight = _inFlightProviders.Values.Select(x => x.CompletionTcs.Task).ToArray();
        }
        else
        {
            var tcs = new TaskCompletionSource<Task[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _uiDispatcher.BeginInvoke(() =>
            {
                try
                {
                    Refresh();
                    tcs.SetResult(_inFlightProviders.Values.Select(x => x.CompletionTcs.Task).ToArray());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            try
            {
                inFlight = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return;
            }
        }

        if (inFlight.Length > 0)
        {
            try
            {
                await Task.WhenAll(inFlight).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private async Task ObserveProviderScan(
        DisplayProvider provider,
        long providerModeVersion,
        Task<CostScanResult> task,
        TaskCompletionSource tcs,
        ICostQueryService queryService)
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
            _uiDispatcher.BeginInvoke(() =>
            {
                try
                {
                    CommitProviderScan(provider, providerModeVersion, task, result, queryService);
                }
                finally
                {
                    tcs.TrySetResult();
                }
            });
        }
        catch
        {
            tcs.TrySetResult();
        }
    }

    private void CommitProviderScan(
        DisplayProvider provider,
        long providerModeVersion,
        Task<CostScanResult> task,
        CostScanResult? result,
        ICostQueryService queryService)
    {
        if (!_inFlightProviders.TryGetValue(provider, out var inFlight)
            || !ReferenceEquals(inFlight.ScanTask, task)
            || inFlight.Version != providerModeVersion)
        {
            return;
        }

        _inFlightProviders.Remove(provider);

        if (result is not null
            && providerModeVersion == CurrentProviderModeVersion(provider)
            && _activeProviders.Contains(provider)
            && queryService.IsCurrent(provider, result.ProviderVersion))
        {
            SetSummary(provider, result.Summary);
            LastUpdated = DateTimeOffset.Now;
        }

        if (providerModeVersion != CurrentProviderModeVersion(provider)
            && _activeProviders.Contains(provider))
        {
            Refresh();
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

    private void Raise(string name)
    {
        if (_uiDispatcher.CheckAccess())
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        else
        {
            _uiDispatcher.BeginInvoke(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
        }
    }
}
