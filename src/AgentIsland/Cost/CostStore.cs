using System.ComponentModel;
using System.Windows.Threading;
using AgentIsland.Core;
using AgentIsland.Model;
using AgentIsland.Usage;

namespace AgentIsland.Cost;

/// Publishes per-provider cost rollups from the local logs. Scans run in
/// parallel off the UI thread on the shared poll cadence; demo mode injects
/// the same screenshot-friendly April numbers as macOS.
public sealed class CostStore : INotifyPropertyChanged
{
    public static CostStore Shared { get; } = new();

    private readonly Dictionary<DisplayProvider, ProviderCostSummary> _summaries = new();
    private DateTimeOffset? _lastUpdated;
    private bool _scanning;
    private DateTimeOffset _scanStartedAt;
    private DispatcherTimer? _pollTimer;

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
    public DateTimeOffset? LastUpdated { get => _lastUpdated; private set { _lastUpdated = value; Raise(nameof(LastUpdated)); } }

    private void SetSummary(DisplayProvider provider, ProviderCostSummary summary)
    {
        _summaries[provider] = summary;
        // Keep the two named accessors' change notifications so existing
        // subscribers (OverviewPage, report cards) refresh exactly as before;
        // guest tiles ride the LastUpdated notification the commit also raises.
        if (provider == DisplayProvider.Claude) Raise(nameof(Claude));
        else if (provider == DisplayProvider.Codex) Raise(nameof(Codex));
    }

    public void StartAutoRefresh()
    {
        Refresh();
        _pollTimer?.Stop();
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(RefreshIntervalStore.Shared.Seconds),
        };
        _pollTimer.Tick += (_, _) => Refresh();
        _pollTimer.Start();
        RefreshIntervalStore.Shared.PropertyChanged += (_, _) =>
        {
            if (_pollTimer is { } timer)
            {
                timer.Interval = TimeSpan.FromSeconds(RefreshIntervalStore.Shared.Seconds);
            }
        };
    }

    public void Refresh()
    {
        if (AppEnvironment.IsDemo)
        {
            InjectDemoData();
            return;
        }
        // The in-flight latch gets a 10-minute wedge escape (macOS 28c9b4c):
        // one scan that never completes must not freeze cost data for the
        // process lifetime — panel numbers were observed stuck six hours
        // behind a hung latch.
        if (_scanning && DateTimeOffset.Now - _scanStartedAt < TimeSpan.FromMinutes(10)) return;
        _scanning = true;
        _scanStartedAt = DateTimeOffset.Now;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var now = DateTimeOffset.Now;
        var lookback = CostSummarizer.YearHistoryDays(now);
        // Five readers, one gate. Grok self-reports dollars, Claude/Codex are
        // table-priced, Cursor yields token counts (no model → no price), and
        // Gemini is an honest empty stub — each summarized off the UI thread
        // and committed together so a slow scan never blocks a fast one twice.
        var claudeTask = Task.Run(() => CostSummarizer.Summarize(ClaudeLogReader.Scan(lookback), now));
        var codexTask = Task.Run(() => CostSummarizer.Summarize(CodexLogReader.Scan(lookback), now));
        var antigravityTask = Task.Run(() => CostSummarizer.Summarize(AntigravityLogReader.Scan(lookback), now));
        var grokTask = Task.Run(() => CostSummarizer.Summarize(GrokLogReader.Scan(lookback), now));
        var cursorTask = Task.Run(() => CostSummarizer.Summarize(CursorLogReader.Scan(lookback), now));
        _ = Task.WhenAll(claudeTask, codexTask, antigravityTask, grokTask, cursorTask).ContinueWith(_ =>
        {
            dispatcher.BeginInvoke(() =>
            {
                if (claudeTask.IsCompletedSuccessfully) SetSummary(DisplayProvider.Claude, claudeTask.Result);
                if (codexTask.IsCompletedSuccessfully) SetSummary(DisplayProvider.Codex, codexTask.Result);
                if (antigravityTask.IsCompletedSuccessfully) SetSummary(DisplayProvider.Antigravity, antigravityTask.Result);
                if (grokTask.IsCompletedSuccessfully) SetSummary(DisplayProvider.Grok, grokTask.Result);
                if (cursorTask.IsCompletedSuccessfully) SetSummary(DisplayProvider.Cursor, cursorTask.Result);
                LastUpdated = DateTimeOffset.Now;
                _scanning = false;
            });
        });
    }

    /// Same hand-tuned April dataset the macOS demo ships: cache-heavy
    /// Claude (10% billable ratio), higher-billable Codex (20%).
    private void InjectDemoData()
    {
        var now = DateTimeOffset.Now;
        SetSummary(DisplayProvider.Claude,
            DemoSummary(now, 146.61, 211_240_000, 21_120_000, 1_510.80, 2_170_000_000, 217_100_000, seed: 7));
        SetSummary(DisplayProvider.Codex,
            DemoSummary(now, 136.50, 164_120_000, 32_820_000, 1_342.60, 1_610_000_000, 322_860_000, seed: 21));
        LastUpdated = now;
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
