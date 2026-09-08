using System.Collections.Concurrent;
using System.ComponentModel;
using AgentIsland.Backend.Cost;
using AgentIsland.Backend.Settings;
using AgentIsland.Backend.Usage;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Storage;
using AgentIsland.Core.Threading;
using AgentIsland.Core.Usage;
using AgentIsland.UI.Providers;
using Xunit;

namespace AgentIsland.Tests;

[Collection("SettingsDiskTests")]
public sealed class StoreDispatcherConcurrencyTests
{
    private sealed class DedicatedThreadDispatcher : IUiDispatcher, IDisposable
    {
        private readonly Thread _thread;
        private readonly BlockingCollection<Action> _queue = new();
        private readonly CancellationTokenSource _cts = new();

        public DedicatedThreadDispatcher()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "DedicatedThreadDispatcher"
            };
            _thread.Start();
        }

        private void Run()
        {
            try
            {
                while (!_cts.IsCancellationRequested && !_queue.IsCompleted)
                {
                    if (_queue.TryTake(out var action, 100, _cts.Token))
                    {
                        try
                        {
                            action();
                        }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        public bool CheckAccess() => Thread.CurrentThread.ManagedThreadId == _thread.ManagedThreadId;

        public void Invoke(Action action)
        {
            if (CheckAccess())
            {
                action();
                return;
            }
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            BeginInvoke(() =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            tcs.Task.GetAwaiter().GetResult();
        }

        public Task InvokeAsync(Action action)
        {
            if (CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            BeginInvoke(() =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }

        public Task InvokeAsync(Func<Task> asyncAction)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            BeginInvoke(async () =>
            {
                try
                {
                    await asyncAction();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }

        public void BeginInvoke(Action action)
        {
            try
            {
                if (!_cts.IsCancellationRequested && !_queue.IsAddingCompleted)
                {
                    _queue.Add(action);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _queue.CompleteAdding(); } catch { }
            _thread.Join(2000);
            _cts.Dispose();
            _queue.Dispose();
        }
    }

    private sealed class TestVisibilityStore : IProviderVisibilityStore
    {
        private readonly List<DisplayProvider> _enabled;
        public event PropertyChangedEventHandler? PropertyChanged;

        public TestVisibilityStore(params DisplayProvider[] enabled)
        {
            _enabled = new List<DisplayProvider>(enabled);
        }

        public IReadOnlyList<DisplayProvider> Enabled => _enabled;
        public IReadOnlyList<DisplayProvider> SlotProviders => _enabled;
        public IReadOnlyList<DisplayProvider> Slots => _enabled;
        public IReadOnlyList<DisplayProvider> Order => _enabled;
        public bool ClaudeVisible
        {
            get => _enabled.Contains(DisplayProvider.Claude);
            set => SetEnabled(DisplayProvider.Claude, value);
        }
        public bool CodexVisible
        {
            get => _enabled.Contains(DisplayProvider.Codex);
            set => SetEnabled(DisplayProvider.Codex, value);
        }
        public bool ClaudeShown => ClaudeVisible;
        public bool CodexShown => CodexVisible;
        public bool ClaudePanelShown => ClaudeVisible;
        public bool CodexPanelShown => CodexVisible;
        public bool AntigravityPanelShown => _enabled.Contains(DisplayProvider.Antigravity);
        public bool GrokPanelShown => _enabled.Contains(DisplayProvider.Grok);
        public bool CursorPanelShown => _enabled.Contains(DisplayProvider.Cursor);
        public bool DeepSeekPanelShown => _enabled.Contains(DisplayProvider.DeepSeek);
        public int GuestPanelCount => 0;
        public bool IsVisible(TriggerTool tool) => true;
        public void RedetectGuests() { }
        public int SelectedCount => _enabled.Count;
        public bool ClaudeDetected => true;
        public bool CodexDetected => true;
        public bool AntigravityDetected => true;
        public bool GrokDetected => false;
        public bool CursorDetected => false;
        public bool DeepSeekDetected => false;
        public bool SetEnabled(DisplayProvider provider, bool enabled)
        {
            if (enabled && !_enabled.Contains(provider))
            {
                _enabled.Add(provider);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
                return true;
            }
            if (!enabled && _enabled.Remove(provider))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
                return true;
            }
            return false;
        }
        public void MoveProvider(int oldIndex, int newIndex) { }
        public bool IsShown(DisplayProvider provider) => _enabled.Contains(provider);
        public bool IsEnabled(DisplayProvider provider) => _enabled.Contains(provider);
        public bool Toggle(DisplayProvider provider) => SetEnabled(provider, !_enabled.Contains(provider));
    }

    private sealed class TestAgentProvider : IAgentProvider, IUsageFetcher
    {
        private readonly Func<CancellationToken, ValueTask<AppUsage>> _fetchFunc;
        public AgentDescriptor Descriptor { get; }

        public TestAgentProvider(string key, string displayName, Func<CancellationToken, ValueTask<AppUsage>> fetchFunc)
        {
            Descriptor = new AgentDescriptor(new AgentKey(key), displayName, AgentCapabilities.Usage);
            _fetchFunc = fetchFunc;
        }

        public ValueTask<AppUsage> FetchUsageAsync(CancellationToken ct = default) => _fetchFunc(ct);
    }

    private sealed class MockCostQueryService : ICostQueryService
    {
        public Func<DisplayProvider, int, DateTimeOffset, CancellationToken, Task<CostScanResult>>? ScanHandler { get; set; }
        public int ScanCount => _scanCount;
        private int _scanCount;
        private readonly Dictionary<DisplayProvider, long> _versions = new();

        public Task<CostScanResult> ScanAsync(DisplayProvider provider, int lookbackDays, DateTimeOffset now, CancellationToken consumerCancellation = default)
        {
            Interlocked.Increment(ref _scanCount);
            if (ScanHandler != null)
            {
                return ScanHandler(provider, lookbackDays, now, consumerCancellation);
            }
            return Task.FromResult(new CostScanResult(
                provider,
                GetVersion(provider),
                now,
                Array.Empty<TokenEvent>(),
                ProviderCostSummary.Empty));
        }

        public Task<CostScanResult> ScanCurrentAsync(DisplayProvider provider, DateTimeOffset now, CancellationToken consumerCancellation = default) =>
            ScanAsync(provider, 30, now, consumerCancellation);

        public void Invalidate(DisplayProvider provider) =>
            _versions[provider] = GetVersion(provider) + 1;

        public bool IsCurrent(DisplayProvider provider, long providerVersion) =>
            GetVersion(provider) == providerVersion;

        public long GetVersion(DisplayProvider provider) =>
            _versions.TryGetValue(provider, out var v) ? v : 0;
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), $"Condition not met within {timeoutMs}ms");
    }

    [Fact]
    public async Task ScenarioA_ConcurrentRefresh_DeduplicatesInFlightPerProvider()
    {
        using var dispatcher = new DedicatedThreadDispatcher();

        // 1. UsageStore deduplication
        var usageTcs = new TaskCompletionSource<AppUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var usageFetchCount = 0;
        var claudeProvider = new TestAgentProvider("claude", "Claude", _ =>
        {
            Interlocked.Increment(ref usageFetchCount);
            return new ValueTask<AppUsage>(usageTcs.Task);
        });

        var visibility = new TestVisibilityStore(DisplayProvider.Claude);
        var usageStore = new UsageStore(
            visibilityStore: visibility,
            refreshIntervalStore: null,
            providers: new[] { claudeProvider },
            uiDispatcher: dispatcher,
            settingsStorage: new MemorySettingsStorage());
        usageStore.DisableInternalTimer = true;

        // Concurrent triggers: 2 manual/timer refreshes and 1 RefreshAsync
        var t1 = Task.Run(() => usageStore.Refresh());
        var t2 = Task.Run(() => usageStore.Refresh());
        var tAsync = Task.Run(async () => await usageStore.RefreshAsync());

        await Task.WhenAll(t1, t2);
        dispatcher.Invoke(() => { });
        await WaitForConditionAsync(() => usageFetchCount >= 1);
        Assert.Equal(1, usageFetchCount);

        var expectedUsage = new AppUsage(new WindowUsage(0.65, null, null), WindowUsage.Unknown, "tier-pro");
        usageTcs.SetResult(expectedUsage);

        await tAsync;
        Assert.Equal(1, usageFetchCount);
        Assert.Equal(0.65, usageStore.Claude.FiveHour.UsedPercent);

        // 2. CostStore deduplication
        var costTcs = new TaskCompletionSource<CostScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var costQuery = new MockCostQueryService
        {
            ScanHandler = (p, _, now, _) => costTcs.Task
        };

        var costStore = new CostStore(
            visibilityStore: visibility,
            intervalStore: null,
            costQueryService: costQuery,
            uiDispatcher: dispatcher);
        costStore.DisableInternalTimer = true;

        var c1 = Task.Run(() => costStore.Refresh());
        var c2 = Task.Run(() => costStore.Refresh());
        var cAsync = Task.Run(async () => await costStore.RefreshAsync());

        await Task.WhenAll(c1, c2);
        dispatcher.Invoke(() => { });
        await WaitForConditionAsync(() => costQuery.ScanCount >= 1);
        Assert.Equal(1, costQuery.ScanCount);

        var expectedCost = new ProviderCostSummary(25.50, 1000, 1000, 100.0, 5000, 5000,
            Array.Empty<double>(), Array.Empty<double>(), Array.Empty<ModelSpend>(),
            Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<DailyTokenBucket>(), Array.Empty<string>());

        costTcs.SetResult(new CostScanResult(DisplayProvider.Claude, 0, DateTimeOffset.Now, Array.Empty<TokenEvent>(), expectedCost));

        await cAsync;
        Assert.Equal(1, costQuery.ScanCount);
        Assert.Equal(25.50, costStore.Claude.TodayDollars);
    }

    [Fact]
    public async Task ScenarioB_DisabledProvider_LateResultDiscarded()
    {
        // 1. UsageStore: disable while in-flight -> late result discarded
        var usageTcs = new TaskCompletionSource<AppUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var claudeProvider = new TestAgentProvider("claude", "Claude", _ => new ValueTask<AppUsage>(usageTcs.Task));
        var visibility = new TestVisibilityStore(DisplayProvider.Claude);
        var usageStore = new UsageStore(
            visibilityStore: visibility,
            refreshIntervalStore: null,
            providers: new[] { claudeProvider },
            uiDispatcher: DirectUiDispatcher.Instance,
            settingsStorage: new MemorySettingsStorage());
        usageStore.DisableInternalTimer = true;

        usageStore.Refresh();
        await WaitForConditionAsync(() => usageStore.Loading);

        // Disable Claude
        visibility.SetEnabled(DisplayProvider.Claude, false);
        usageStore.Refresh();
        Assert.Equal(AppUsage.Empty, usageStore.Claude);

        // Background fetch finishes late
        usageTcs.SetResult(new AppUsage(new WindowUsage(0.85, null, null), WindowUsage.Unknown, "pro"));
        await Task.Delay(50);

        // State must remain Empty, not resurrected
        Assert.Equal(AppUsage.Empty, usageStore.Claude);
        Assert.False(usageStore.Loading);

        // 2. CostStore: disable while in-flight -> late result discarded
        var costTcs = new TaskCompletionSource<CostScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var costQuery = new MockCostQueryService
        {
            ScanHandler = (p, _, now, _) => costTcs.Task
        };
        var costStore = new CostStore(
            visibilityStore: visibility,
            intervalStore: null,
            costQueryService: costQuery,
            uiDispatcher: DirectUiDispatcher.Instance);
        costStore.DisableInternalTimer = true;

        visibility.SetEnabled(DisplayProvider.Claude, true);
        costStore.Refresh();
        await WaitForConditionAsync(() => costQuery.ScanCount >= 1);

        visibility.SetEnabled(DisplayProvider.Claude, false);
        costStore.Refresh();
        Assert.Equal(ProviderCostSummary.Empty, costStore.Claude);

        var lateCost = new ProviderCostSummary(99.0, 100, 100, 99.0, 100, 100,
            Array.Empty<double>(), Array.Empty<double>(), Array.Empty<ModelSpend>(),
            Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<DailyTokenBucket>(), Array.Empty<string>());

        costTcs.SetResult(new CostScanResult(DisplayProvider.Claude, 0, DateTimeOffset.Now, Array.Empty<TokenEvent>(), lateCost));
        await Task.Delay(50);

        Assert.Equal(ProviderCostSummary.Empty, costStore.Claude);
    }

    [Fact]
    public async Task ScenarioC_NewGeneration_SupersedesOldLateResult()
    {
        // 1. UsageStore: generation 0 late result does not overwrite generation 1 result
        var tcs0 = new TaskCompletionSource<AppUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs1 = new TaskCompletionSource<AppUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;

        var claudeProvider = new TestAgentProvider("claude", "Claude", _ =>
        {
            var count = Interlocked.Increment(ref callCount);
            return new ValueTask<AppUsage>(count == 1 ? tcs0.Task : tcs1.Task);
        });

        var visibility = new TestVisibilityStore(DisplayProvider.Claude);
        var usageStore = new UsageStore(
            visibilityStore: visibility,
            refreshIntervalStore: null,
            providers: new[] { claudeProvider },
            uiDispatcher: DirectUiDispatcher.Instance,
            settingsStorage: new MemorySettingsStorage());
        usageStore.DisableInternalTimer = true;

        usageStore.Refresh(); // Starts generation 0
        await WaitForConditionAsync(() => callCount >= 1);
        Assert.Equal(1, callCount);

        // Trigger new generation by disabling and re-enabling
        visibility.SetEnabled(DisplayProvider.Claude, false);
        usageStore.Refresh();
        visibility.SetEnabled(DisplayProvider.Claude, true);
        usageStore.Refresh(); // Starts generation 1
        await WaitForConditionAsync(() => callCount >= 2);
        Assert.Equal(2, callCount);

        // Generation 1 completes first
        var gen1Usage = new AppUsage(new WindowUsage(0.91, null, null), WindowUsage.Unknown, "new-tier");
        tcs1.SetResult(gen1Usage);
        await Task.Delay(50);
        Assert.Equal(0.91, usageStore.Claude.FiveHour.UsedPercent);

        // Generation 0 completes late
        var gen0Usage = new AppUsage(new WindowUsage(0.12, null, null), WindowUsage.Unknown, "old-tier");
        tcs0.SetResult(gen0Usage);
        await Task.Delay(50);

        // Must still be gen1Usage, NOT overwritten by old generation 0
        Assert.Equal(0.91, usageStore.Claude.FiveHour.UsedPercent);

        // 2. CostStore: version 0 late result does not overwrite version 1 result
        var costTcs0 = new TaskCompletionSource<CostScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var costTcs1 = new TaskCompletionSource<CostScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var costScanCount = 0;

        var costQuery = new MockCostQueryService
        {
            ScanHandler = (p, _, now, _) =>
            {
                var count = Interlocked.Increment(ref costScanCount);
                return count == 1 ? costTcs0.Task : costTcs1.Task;
            }
        };

        var costStore = new CostStore(
            visibilityStore: visibility,
            intervalStore: null,
            costQueryService: costQuery,
            uiDispatcher: DirectUiDispatcher.Instance);
        costStore.DisableInternalTimer = true;

        costStore.Refresh(); // scan 1
        await WaitForConditionAsync(() => costScanCount >= 1);
        Assert.Equal(1, costScanCount);

        // Bump version
        visibility.SetEnabled(DisplayProvider.Claude, false);
        costStore.Refresh();
        visibility.SetEnabled(DisplayProvider.Claude, true);
        costStore.Refresh(); // scan 2
        await WaitForConditionAsync(() => costScanCount >= 2);
        Assert.Equal(2, costScanCount);

        var costSummary1 = new ProviderCostSummary(88.0, 100, 100, 88.0, 100, 100,
            Array.Empty<double>(), Array.Empty<double>(), Array.Empty<ModelSpend>(),
            Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<DailyTokenBucket>(), Array.Empty<string>());

        costTcs1.SetResult(new CostScanResult(DisplayProvider.Claude, costQuery.GetVersion(DisplayProvider.Claude), DateTimeOffset.Now, Array.Empty<TokenEvent>(), costSummary1));
        await Task.Delay(50);
        Assert.Equal(88.0, costStore.Claude.TodayDollars);

        var costSummary0 = new ProviderCostSummary(11.0, 100, 100, 11.0, 100, 100,
            Array.Empty<double>(), Array.Empty<double>(), Array.Empty<ModelSpend>(),
            Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<DailyTokenBucket>(), Array.Empty<string>());

        costTcs0.SetResult(new CostScanResult(DisplayProvider.Claude, 0, DateTimeOffset.Now, Array.Empty<TokenEvent>(), costSummary0));
        await Task.Delay(50);

        // Must still be costSummary1, not overwritten by old version
        Assert.Equal(88.0, costStore.Claude.TodayDollars);
    }

    [Fact]
    public async Task ScenarioD_RefreshAsync_WaitsForUICommitOnThreadPool_AndNoDeadlockOnUIThread()
    {
        using var dispatcher = new DedicatedThreadDispatcher();
        var expectedUsage = new AppUsage(new WindowUsage(0.77, null, null), WindowUsage.Unknown, "pro");
        var claudeProvider = new TestAgentProvider("claude", "Claude", async ct =>
        {
            await Task.Delay(20, ct);
            return expectedUsage;
        });

        var visibility = new TestVisibilityStore(DisplayProvider.Claude);
        var usageStore = new UsageStore(
            visibilityStore: visibility,
            refreshIntervalStore: null,
            providers: new[] { claudeProvider },
            uiDispatcher: dispatcher,
            settingsStorage: new MemorySettingsStorage());
        usageStore.DisableInternalTimer = true;

        var expectedCost = new ProviderCostSummary(44.44, 200, 200, 44.44, 200, 200,
            Array.Empty<double>(), Array.Empty<double>(), Array.Empty<ModelSpend>(),
            Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<DailyTokenBucket>(), Array.Empty<string>());

        var costQuery = new MockCostQueryService
        {
            ScanHandler = async (p, _, now, ct) =>
            {
                await Task.Delay(20, ct);
                return new CostScanResult(p, 0, now, Array.Empty<TokenEvent>(), expectedCost);
            }
        };

        var costStore = new CostStore(
            visibilityStore: visibility,
            intervalStore: null,
            costQueryService: costQuery,
            uiDispatcher: dispatcher);
        costStore.DisableInternalTimer = true;

        // 1. ThreadPool caller awaiting RefreshAsync: UI state MUST be fully committed upon return
        await Task.Run(async () =>
        {
            await usageStore.RefreshAsync();
            Assert.Equal(0.77, usageStore.Claude.FiveHour.UsedPercent);

            await costStore.RefreshAsync();
            Assert.Equal(44.44, costStore.Claude.TodayDollars);
        });

        // 2. Dispatcher thread caller awaiting RefreshAsync: no synchronous deadlock
        var uiThreadTest = dispatcher.InvokeAsync(async () =>
        {
            await usageStore.RefreshAsync();
            Assert.Equal(0.77, usageStore.Claude.FiveHour.UsedPercent);

            await costStore.RefreshAsync();
            Assert.Equal(44.44, costStore.Claude.TodayDollars);
        });

        await uiThreadTest.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ScenarioE_SingleProviderFailure_DoesNotBlockOtherProvidersOrHang()
    {
        // 1. UsageStore: Claude fails with exception, Codex succeeds
        var claudeProvider = new TestAgentProvider("claude", "Claude", _ =>
            throw new InvalidOperationException("Claude upstream service 503"));

        var codexUsage = new AppUsage(new WindowUsage(0.48, null, null), WindowUsage.Unknown, "pro");
        var codexProvider = new TestAgentProvider("codex", "Codex", _ =>
            ValueTask.FromResult(codexUsage));

        var visibility = new TestVisibilityStore(DisplayProvider.Claude, DisplayProvider.Codex);
        var usageStore = new UsageStore(
            visibilityStore: visibility,
            refreshIntervalStore: null,
            providers: new[] { claudeProvider, codexProvider },
            uiDispatcher: DirectUiDispatcher.Instance,
            settingsStorage: new MemorySettingsStorage());
        usageStore.DisableInternalTimer = true;

        // RefreshAsync must not throw unhandled exception or hang
        await usageStore.RefreshAsync();

        // Codex updated successfully
        Assert.Equal(0.48, usageStore.Codex.FiveHour.UsedPercent);
        // Claude captured error without killing coordinator
        Assert.NotNull(usageStore.Claude.FiveHour.Error);
        Assert.False(usageStore.Loading);

        // 2. CostStore: Claude scan throws, Codex scan succeeds
        var codexCost = new ProviderCostSummary(33.10, 500, 500, 33.10, 500, 500,
            Array.Empty<double>(), Array.Empty<double>(), Array.Empty<ModelSpend>(),
            Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<DailyTokenBucket>(), Array.Empty<string>());

        var costQuery = new MockCostQueryService
        {
            ScanHandler = (p, _, now, _) =>
            {
                if (p == DisplayProvider.Claude)
                {
                    return Task.FromException<CostScanResult>(new System.IO.IOException("Corrupt database log"));
                }
                return Task.FromResult(new CostScanResult(p, 0, now, Array.Empty<TokenEvent>(), codexCost));
            }
        };

        var costStore = new CostStore(
            visibilityStore: visibility,
            intervalStore: null,
            costQueryService: costQuery,
            uiDispatcher: DirectUiDispatcher.Instance);
        costStore.DisableInternalTimer = true;

        await costStore.RefreshAsync();

        Assert.Equal(33.10, costStore.Codex.TodayDollars);
        Assert.Equal(ProviderCostSummary.Empty, costStore.Claude);
    }
}
